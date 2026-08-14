# M6 子进程架构重构 — 壁纸 + 图标层独立进程（Lively 方案）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 将壁纸窗口和图标层窗口拆为**独立子进程**（参考 Lively 架构），主进程拿到子进程 hwnd 后 `SetParent` 到 WorkerW/SHELLDLL_DefView，成为桌面子窗口。子进程渲染不受影响（不同进程的窗口 SetParent 无 WPF 渲染冲突），Win+D 不影响（桌面子窗口天然免疫 ShowDesktop）。移除 WH_KEYBOARD_LL 键盘钩子（不再需要）。

**Architecture:**
- **DesktopManager.App（主进程）**：托盘 + 设置窗口 + 配置 + 桌面同步 + 播放治理。通过 `Process.Start` 启动子进程，通过 stdout/stdin JSON 行协议通信（拿 hwnd、切壁纸、图标同步、播放控制）。拿到子进程 hwnd 后调 `SetupDesktopLayer` + `SetParent` 挂到桌面窗口树。
- **DesktopManager.Player.Wallpaper（壁纸子进程）**：独立 WPF `WinExe` 进程。构造全屏无边框窗口，内容按指令渲染（静态图/GIF/视频），裁剪偏移由主进程下发。启动后 stdout 输出 `{"type":"ready","hwnd":12345}`，之后 stdin 收 `{"cmd":"setWallpaper","path":"...","kind":"image"}` / `{"cmd":"pause"}` / `{"cmd":"resume"}` / `{"cmd":"setPosition","x":0,"y":0,"w":1920,"h":1080}`。
- **DesktopManager.Player.Icons（图标层子进程）**：独立 WPF `WinExe` 进程。构造全屏无边框窗口，承载 IconLayerWindow + FenceControl 的渲染和交互逻辑。启动后 stdout 输出 hwnd，之后 stdin 收图标同步 / Fence 管理 / 选中态指令。鼠标键盘交互在子进程内自闭环（双击打开/右键菜单/拖拽归属），归属/布局变更通过 stdout 事件通知主进程持久化。
- **IPC 协议**：基于 stdin/stdout 的 JSON 行协议（每行一条 JSON）。主进程→子进程用 stdin，子进程→主进程用 stdout。关掉 stdin = 终止子进程。

**Tech Stack:** C# / WPF / .NET 10 / xUnit / System.Text.Json / Win32 SetParent + 0x052C WorkerW。

## Global Constraints

- 主进程不直接创建任何全屏窗口（所有渲染窗口都是子进程的）。
- 子进程是 `WinExe`（无控制台），stdout/stdin 通过 `ProcessStartInfo.RedirectStandardInput/Output` 重定向。
- 子进程退出码：0=正常退出，非0=异常（主进程根据需要重启）。
- 主进程崩溃时子进程应自动退出（子进程检测 stdin EOF 后 Shutdown）。
- IPC 消息：紧凑 JSON 行，用 `type`/`cmd` 字段区分。版本化留 `v` 字段（初始 `"v":1`）。
- SetParent 时机：子进程窗口 `SourceInitialized` 后输出 hwnd → 主进程收到后调 `SetupDesktopLayer` + `SetParent`。子进程窗口在输出 hwnd 前保持隐藏（`Visibility=Hidden`），SetParent 后由主进程发 `{"cmd":"show"}` 让其显示。
- 图标层子进程 SetParent 到 WorkerW 后，Z-order 在 SHELLDLL_DefView 之下（壁纸之上）。桌面图标已隐藏（现有机制不变），用户看到的是子进程绘制的图标。
- 移除 WH_KEYBOARD_LL 钩子（不再需要对抗 Win+D）。
- 精确 `git add <paths>`；Core 纯逻辑 TDD；子进程 UI = 真机验收。

## 任务总览

| 执行单元 | 任务 | 性质 | 验收 | 依赖 |
|---|---|---|---|---|
| M6-T1 | IPC 消息模型 + 协议定义 | TDD | 单测：消息序列化/反序列化 round-trip | — |
| M6-T2 | DesktopManager.Player.Wallpaper 项目骨架 | UI | 子进程启动→输出 hwnd→主进程收到 | T1 |
| M6-T3 | 主进程：子进程管理器 + SetParent 到 WorkerW | 重构 | 双屏壁纸通过子进程渲染 + Win+D 不影响 | T2 |
| M6-T4 | 壁纸子进程：图/GIF/视频渲染 + 裁剪偏移 | 重构 | 双屏静态图/视频/GIF 正常 + 跨屏拼接 | T3 |
| M6-T5 | 壁纸播放治理迁移（全屏/电池/锁屏暂停） | 重构 | 全屏应用→壁纸暂停（通过 IPC 下发） | T4 |
| M6-T6 | DesktopManager.Player.Icons 项目骨架 | UI | 子进程启动→输出 hwnd→主进程收到 | T3 |
| M6-T7 | 图标层子进程：图标/收纳盒渲染 + 交互 | 重构 | 图标显示/双击/右键/拖拽/Fence 全通 | T6 |
| M6-T8 | 图标同步 + 持久化迁移（主进程↔子进程 IPC） | 重构 | 文件增删→子进程同步 + 布局持久化 | T7 |
| M6-T9 | 设置窗口改造（通过 IPC 操作子进程） | 重构 | 设置窗口换壁纸/管理组→子进程即时生效 | T8 |
| M6-T10 | 移除键盘钩子 + 清理旧代码 + 冒烟 + tag | 集成 | 全流程 + Win+D 不影响 + 托盘退出恢复 | T1–T9 |

## 文件结构（M6 新增/改动）

```
src/DesktopManager.Ipc/
├── Messages.cs              # 新：所有 IPC 消息 record 定义
├── IpcReader.cs             # 新：从 Stream 读 JSON 行
├── IpcWriter.cs             # 新：向 Stream 写 JSON 行
├── IpcChannel.cs            # 新：双向通道（stdin+stdout 封装）
└── DesktopManager.Ipc.csproj # 新：net10.0 类库
src/DesktopManager.Player.Wallpaper/
├── App.xaml(.cs)            # 新：子进程入口（读 stdin args、创建窗口）
├── WallpaperWindow.xaml(.cs)# 新/搬：从现有 WallpaperWindow 搬逻辑
├── DesktopManager.Player.Wallpaper.csproj # 新：WinExe net10.0-windows
src/DesktopManager.Player.Icons/
├── App.xaml(.cs)            # 新：子进程入口
├── IconLayerWindow.xaml(.cs)# 新/搬：从现有 IconLayerWindow 搬逻辑
├── Controls/FenceControl.*  # 新/搬：从现有 FenceControl 搬
├── DesktopManager.Player.Icons.csproj # 新：WinExe net10.0-windows
src/DesktopManager.App/
├── Services/
│   ├── ChildProcessManager.cs   # 新：启动/管理子进程生命周期
│   └── DesktopLayerHost.cs      # 新：SetupDesktopLayer + SetParent 编排
├── MultiMonitorHost.cs          # 改：不再直接创建窗口，改为启动子进程
├── App.xaml.cs                  # 改：移除键盘钩子；改用 ChildProcessManager
└── Windows/SettingsWindow.*     # 改：壁纸/组操作改通过 IPC 下发
src/DesktopManager.Native/
└── WindowInterop.cs             # 改：+ SetupDesktopLayer + AttachToDesktop（精简版）
src/DesktopManager.Tests/
└── IpcTests.cs                  # 新：IPC 消息 round-trip
```

## 详细任务

### M6-T1 — IPC 消息模型 + 协议定义（TDD）

- [ ] 新建 `DesktopManager.Ipc` 类库项目（net10.0，引用 System.Text.Json）。
- [ ] `Messages.cs`：定义所有消息 record：
  - 子进程→主进程：`Ready { hwnd }`、`LayoutChanged { fences, positions }`、`IconOpened { path }`、`Error { message }`
  - 主进程→子进程：`SetWallpaper { path, kind, cropX?, cropY?, cropW?, cropH? }`、`SetIcons { items }`、`ApplyDiff { added, removed }`、`SetFences { fences }`、`Pause`、`Resume`、`Show`、`SetPosition { x, y, w, h }`、`Shutdown`
  - 所有消息带 `string Type` 字段做多态反序列化（`JsonDerivedType` 或手动 `switch`）。
- [ ] `IpcReader`：`async Task<IpcMessage?> ReadAsync(Stream)`，按行读取 + 反序列化。
- [ ] `IpcWriter`：`void Write(Stream, IpcMessage)`，序列化 + 换行 + flush。
- [ ] `IpcChannel`：封装 Process 的 stdin+stdout，提供 `Send`/`Receive` + `OnStdError`。
- [ ] 测试（先红）`IpcTests`：每类消息序列化→反序列化 round-trip；多态 Type 字段正确分发。
- [ ] TDD 红→绿→commit。

### M6-T2 — WallpaperPlayer 项目骨架

- [ ] 新建 `DesktopManager.Player.Wallpaper` 项目（WinExe，net10.0-windows，引用 Ipc）。
- [ ] `App.xaml.cs`：
  - 解析命令行参数（`--monitor-x 0 --monitor-y 0 --monitor-w 1920 --monitor-h 1080`）。
  - 创建全屏无边框窗口（`Visibility=Hidden`，等主进程 `Show` 指令）。
  - `SourceInitialized` 后：stdout 输出 `Ready { hwnd }`。
  - 启动 stdin 监听循环（后台 Task）：收到 `SetWallpaper` → 加载渲染；收到 `Show` → `Visibility=Visible`；收到 `Pause/Resume` → 控制；收到 `Shutdown` → 退出。
  - stdin EOF → `Shutdown()`。
- [ ] `WallpaperWindow.xaml(.cs)`：从现有 WallpaperWindow 搬渲染逻辑（Image/MediaElement/GIF），移除 Win32 样式操作（SetParent 由主进程负责）。
- [ ] 验收（主进程手动测试）：手动 `Process.Start` 启动子进程，观察 stdout 输出 hwnd；手动 stdin 发 `Show`，窗口可见。
- [ ] commit。

### M6-T3 — 主进程：子进程管理 + SetParent 到 WorkerW

- [ ] `ChildProcessManager.cs`：管理壁纸子进程（每屏一个），`Process.Start` + 重定向 stdin/stdout + 生命周期（退出/重启）。
- [ ] `DesktopLayerHost.cs`：`SetupDesktopLayer()`（发送 0x052C + 找 WorkerW）；`AttachToDesktop(IntPtr childHwnd)`（SetParent 到 WorkerW）。
- [ ] `MultiMonitorHost.cs` 改造：`Attach` 不再创建 `WallpaperWindow`，改为每屏启动一个 `ChildProcessManager`（WallpaperPlayer）；收到 `Ready { hwnd }` 后调 `DesktopLayerHost.AttachToDesktop(hwnd)` + 发 `Show`。
- [ ] 验收（双屏）：子进程壁纸窗口显示在 WorkerW 下；**按 Win+D → 壁纸窗口不受影响**（核心验收点）。
- [ ] commit。

### M6-T4 — 壁纸子进程：完整渲染 + 跨屏裁剪

- [ ] WallpaperPlayer 子进程完善：
  - `SetWallpaper` 指令处理：路径 + kind + 可选裁剪 rect（跨屏组模式下主进程下发裁剪偏移）。
  - 图片/GIF/视频三种渲染（从现有 WallpaperWindow 搬 ApplyImage/ApplyGif/ApplyVideo 逻辑）。
  - `SetPosition` 指令处理（拓扑变化后主进程下发新位置/尺寸）。
- [ ] 主进程：`ApplyWallpaperTo` 改为通过 IPC `SetWallpaper` 下发给子进程；跨屏组模式下计算裁剪 rect 并下发。
- [ ] 验收（双屏）：静态图 + GIF + 视频 + 跨屏拼接 全部正常。
- [ ] commit。

### M6-T5 — 播放治理迁移（IPC 下发暂停/恢复）

- [ ] `PlaybackGovernor` 改造：不再直接调 `WallpaperWindow.Pause/Resume`，改为通过 IPC 向壁纸子进程发 `Pause/Resume`。
- [ ] 验收：全屏应用/锁屏/电池 → 壁纸暂停（子进程收到 IPC 指令）；恢复后继续播放。
- [ ] commit。

### M6-T6 — IconPlayer 项目骨架

- [ ] 新建 `DesktopManager.Player.Icons` 项目（WinExe，net10.0-windows，引用 Ipc）。
- [ ] `App.xaml.cs`：同 T2 模式——解析参数、创建窗口、输出 hwnd、监听 stdin。
- [ ] `IconLayerWindow.xaml(.cs)` + `FenceControl.xaml(.cs)`：从现有项目搬全部渲染和交互逻辑。
- [ ] 主进程：每屏启动一个 IconPlayer 子进程；收到 hwnd 后 SetParent 到 SHELLDLL_DefView 之下（图标在壁纸之上）。
- [ ] 验收（双屏）：图标层子进程窗口显示；图标渲染正常；**Win+D → 图标层不受影响**。
- [ ] commit。

### M6-T7 — 图标层子进程：渲染 + 交互自闭环

- [ ] IconPlayer 子进程完善：
  - 图标渲染：接收 `SetIcons` 初始化全量 + `ApplyDiff` 增量。
  - Fence 管理：接收 `SetFences` 初始化；用户操作（新建/删除/拖拽/折叠/重命名）在子进程内处理。
  - 选中态：子进程内自闭环（单选高亮 + 跨屏通过主进程中转清除）。
  - 双击打开/右键菜单：子进程内处理（文件操作通过 IPC 委托主进程，避免子进程直接操作文件系统）。
  - 拖拽：子进程内处理 Fence↔散落拖拽；跨屏拖拽通过主进程中转。
- [ ] 验收（双屏）：图标/收纳盒全套交互（显示/双击/右键/拖拽/折叠/选中）全通。
- [ ] commit。

### M6-T8 — 图标同步 + 持久化迁移

- [ ] 主进程 `DesktopSync` 改造：`Changed` 事件 → 通过 IPC 向 IconPlayer 子进程发 `ApplyDiff`。
- [ ] IconPlayer 子进程：布局变更 → stdout 发 `LayoutChanged { fences, positions }` → 主进程收到后持久化（ConfigStore 防抖保存）。
- [ ] 验收：桌面文件增删/改名 → 图标层实时同步；拖拽/新建/删除 Fence → 重启保持。
- [ ] commit。

### M6-T9 — 设置窗口改造（IPC 下发）

- [ ] 设置窗口的壁纸/组操作改为通过 `ChildProcessManager` IPC 下发给子进程。
- [ ] 验收：设置窗口换壁纸/管理组 → 子进程即时生效 + 重启保持。
- [ ] commit。

### M6-T10 — 移除钩子 + 清理 + 冒烟 + tag

- [ ] 移除 `WH_KEYBOARD_LL` 键盘钩子 + 所有相关代码。
- [ ] 移除旧的 `WallpaperWindow`/`IconLayerWindow`（已被子进程替代）。
- [ ] 移除 Z 看门狗（不再需要——子进程窗口在 WorkerW 下，不会浮高）。
- [ ] 移除壁纸窗底部 2px 缝（不再需要——WorkerW 子窗口不触发全屏检测）。
- [ ] explorer 重启处理：TaskbarCreated → 重启子进程 + 重新 SetParent。
- [ ] build + test 全绿。
- [ ] 双屏冒烟清单：
  1. 启动：壁纸 + 图标层通过子进程渲染
  2. **Win+D → 壁纸和图标层完全不受影响**（核心验收）
  3. 图标双击/右键/拖拽/收纳盒/选中 全通
  4. 跨屏拼接/视频同步/组管理 全通
  5. 全屏暂停/锁屏暂停/电池暂停 全通
  6. 拔插屏 子进程重启 + 恢复
  7. explorer 重启 子进程重启 + 恢复
  8. 托盘退出 子进程退出 + 原生桌面恢复
  9. 任务栏不显示任何窗口
- [ ] `git tag m6-childprocess`。

## 风险与对策

| 风险 | 严重度 | 对策 |
|---|---|---|
| WPF 子进程 SetParent 后仍渲染异常 | 🟠 高 | Lively 已验证 WPF 子进程可行（其 Player.Wmf 就是 WPF）；先 T3 验证再推进 |
| IPC 性能（图标同步延迟） | 🟡 中 | JSON 行协议足够快；全量初始化 + 增量 diff 两段式 |
| 子进程崩溃恢复 | 🟡 中 | 主进程监控 Process.Exited，自动重启 + 恢复状态 |
| 图标层交互跨进程复杂度 | 🟠 高 | 交互逻辑尽量在子进程内自闭环；跨屏操作通过主进程中转 |
| SetParent 到 SHELLDLL_DefView 下方可能影响桌面图标 | 🟡 中 | 桌面图标已隐藏（HideIcons + SysListView32 隐藏），不冲突 |
| 打包体积增大（3 个 exe） | 🟢 低 | MSIX 支持多 exe；体积增加可接受 |

## Self-Review

- 与 Lively 架构对齐：主进程管逻辑 + IPC，子进程管渲染 + SetParent 到 WorkerW。
- Win+D 核心问题解决：子进程窗口是 WorkerW 的子窗口，ShowDesktop 不影响。
- 图标层也做子进程：图标层和壁纸层都挂到桌面窗口树，统一不受 Win+D 影响。
- 交互体验：图标层子进程内自闭环大部分交互（双击/右键/拖拽/选中），跨屏通过主进程中转。
- 崩溃安全不变式保持：主进程崩溃 → 子进程 stdin EOF → 子进程自动退出 → 桌面恢复。
- explorer 重启：TaskbarCreated → 主进程重启子进程 + 重新 SetParent。
