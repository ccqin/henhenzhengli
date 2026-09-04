# DesktopManager

> 🏪 **已上架微软商店**：[狠狠整理](https://apps.microsoft.com/detail/9NQZ7KPDSZ01?hl=zh-cn&gl=US&ocid=pdpshare)

Windows 桌面图标管理 + 动态壁纸结合体工具。接管 explorer 桌面图标显示，自绘图标层提供收纳盒/分组管理；壁纸层支持静态图与视频/GIF，支持多显示器与跨屏组合。以 MSIX + runFullTrust 形式分发（微软商店已上架，见顶部链接）。

**技术栈**：C# / WPF / .NET 10 / MSIX / xUnit / Win32 P/Invoke。

## 现状

| 里程碑 | 状态 | tag |
|---|---|---|
| M0 骨架 + 三件套 spike + 托盘常驻 | ✅ 完成 | `m0-skeleton` |
| M1 桌面接管 + 图标镜像核心 | ✅ 完成（真机验收通过） | `m1-desktop-takeover` |
| M2 收纳盒/分组 + 双击隐藏 | ✅ 完成（真机验收通过） | `m2-fences` |
| M3 多屏（图标层） | ✅ 完成（双屏真机验收通过） | `m3-multimon` |
| M4 壁纸层（单屏×每屏） | ✅ 完成（双屏真机验收通过） | `m4-wallpaper` |
| M5 跨屏壁纸（显示组）+ 设置窗口 | ✅ 完成（双屏真机验收；拓扑应用本机受限已降级） | `m5-crossscreen` |
| M6 子进程架构重构（壁纸/图标层独立进程 + Owner 免疫 Win+D） | ✅ 完成（真机验收通过） | `m6-childprocess` |
| M7 商店化 | 📋 路线图 | — |

详见 `docs/superpowers/plans/`。

## 环境要求

- **.NET 10 SDK**（开发机已验证 10.0.302）
- **Windows 10 1809+**（主测 Windows 11）

## 构建与运行

```bash
# 在项目根目录执行
dotnet build DesktopManager.sln

# 运行（会先 build，再启动 app）
dotnet run --project src/DesktopManager.App
```

**用 Visual Studio 调试**：打开 `DesktopManager.sln`，把 `DesktopManager.App` 设为启动项目，F5 运行（可在 Output 窗口看 `Debug.WriteLine` 日志、设断点）。

## 测试

```bash
dotnet test DesktopManager.sln
```

## 项目结构

```
src/
├── DesktopManager.Core/             # 纯逻辑（可单测），net10.0：Models / Services（IconItem、DesktopSnapshot、DesktopDiff、DesktopSync、ConfigStore）
├── DesktopManager.Native/           # Win32 P/Invoke 封装：DesktopIconVisibility、MonitorEnumerator、WindowInterop（Owner 挂载/Z 序）
├── DesktopManager.Ipc/              # M6：子进程 JSON 行协议（消息模型 + Reader/Writer/Channel）
├── DesktopManager.Player.Wallpaper/ # M6：壁纸子进程（每屏一个，图/GIF/视频渲染 + 跨屏裁剪）
├── DesktopManager.Player.Icons/     # M6：图标层子进程（每屏一个，图标/收纳盒渲染 + 交互自闭环）
├── DesktopManager.App/              # 主进程：托盘、设置窗口、子进程生命周期（ChildProcessManager）、聚合持久化、播放治理
└── DesktopManager.Tests/            # xUnit 单测（含 IPC round-trip）
```

**M6 架构**：主进程管逻辑与生命周期，渲染全部在子进程；壁纸窗口（最底）→ 图标层窗口（其上）→ 普通窗口/设置 UI（最上）。主进程崩溃时子进程因 stdin EOF 自动退出（桌面安全恢复）。

## 实现原理（M6 终态架构）

### 进程模型：1 主 + 每屏 2 子

```
DesktopManager.App（主进程）
 ├─ 托盘 / 设置窗口 / 配置 / 桌面文件监听（DesktopSync diff）/ 播放治理（全屏·锁屏·电池暂停）
 ├─ 每屏 1 个 Player.Wallpaper.exe —— 壁纸渲染（静态图 / GIF / 视频，跨屏拼接裁剪）
 └─ 每屏 1 个 Player.Icons.exe —— 图标层渲染 + 交互（双击/右键/拖拽/收纳盒，子进程内自闭环）
```

通信走 **stdin/stdout JSON 行协议**（`DesktopManager.Ipc`）：子进程启动后先输出 `{"type":"ready","hwnd":...}` 上报窗口句柄，之后主进程下发壁纸/图标/暂停等指令，子进程上报布局变更、跨屏拖拽请求。**崩溃安全不变式**：主进程死 → 子进程 stdin 断开 → 自动退出 → 原生桌面恢复。

### 核心技巧：窗口 Owner = SHELLDLL_DefView（Win+D 免疫的关键）

子进程窗口是**普通顶层窗口**，创建后由主进程执行（`WindowInterop.AttachTopLevel`）：

```csharp
SetWindowLongPtr(hwnd, GWL_HWNDPARENT, hShellDefView);  // ① Owner 设为桌面图标视图
// ② WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE（不进任务栏、不抢焦点；壁纸再加 WS_EX_TRANSPARENT 点击穿透）
// ③ 主进程 BottomPair 编排：图标层贴底、壁纸窗插其正下方
```

①是 **Stardock Fences / Layouter 同款技巧**，一举解决三个问题：

| 特性 | 原理 |
|---|---|
| **Win+D / 显示桌面免疫** | owned 窗口跟随 owner；owner 是 shell 自己的 DefView，从不被"显示桌面"最小化 → 我们的窗口跟着免疫（真实键盘实测） |
| **Z 序天然贴桌面层** | owned 窗口约束在 owner 之上，永远低于普通窗口 |
| **无跨进程渲染问题** | owner 是顶层窗口间关系，不是 `SetParent` 父子挂载 |

### 为什么不用 Lively 的 WorkerW 挂载？（真机踩坑记录）

Lively 等壁纸软件的通用方案是把渲染窗口 `SetParent` 进桌面窗口树（WorkerW/Progman）——**在部分显卡驱动（本机 Intel A780）上跨进程子窗口内容不进物理显示输出**：DWM 合成缓冲里有内容（截图/PrintWindow 都"看得到"），但显示器不显示（人眼验证）。真机排障全过程与三条路线对比见 `docs/2026-08-18-M6子进程重构-真机复盘.md`。**教训**：截图链不能作为"物理显示"的验收手段。

### 保活机制

- **Z 看门狗**（2s）：检测窗口浮高 → 重锚底序；
- **子进程崩溃** → 非零退出码自动重启 + 数据恢复；
- **explorer 重启**（TaskbarCreated）→ 重建全部子进程并重新挂 Owner；
- **拔插屏** → 按新拓扑停/起对应子进程，孤儿屏配置保留待插回恢复。

## ⚠️ 真机验收注意

启动后**会接管 explorer 桌面图标**（隐藏原生图标、由 app 的图标层接管显示），正常托盘退出会恢复。
Win11 真机验收结论（已通过）：原生图标立即隐藏（直接隐藏 SysListView32）、图标层在文件夹窗口下层（桌面层 Z-order）、重启 explorer 只重启一次且继续接管（单实例 Mutex 断环）。

**如果 app 异常退出导致桌面图标没恢复**：正常情况无需手动处理——RunOnce 自清理会在下次登录时自动修复，或直接再启动一次 app（接管状态自动恢复）。手动恢复用一行命令：

```bash
# 推荐：一键恢复 + 自动刷新桌面
DesktopManager.App.exe --restore-icons

# 最后兜底（极端情况，手动清注册表）
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce" /v DM_RestoreIcons /f
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v HideIcons /t REG_DWORD /d 0 /f
```

## 文档

- **架构图与分层说明：`docs/ARCHITECTURE.md`**
- 总计划与里程碑路线图：`docs/superpowers/plans/2026-07-21-desktop-manager.md`
- M1 实现计划：`docs/superpowers/plans/2026-07-22-m1-desktop-takeover.md`
- M2 实现计划：`docs/superpowers/plans/2026-07-22-m2-fences-hideicons.md`
- M3 多屏实现计划：`docs/superpowers/plans/2026-08-04-m3-multi-monitor.md`
- M4 壁纸层实现计划：`docs/superpowers/plans/2026-08-04-m4-wallpaper.md`
- M5 跨屏壁纸+设置窗口计划：`docs/superpowers/plans/2026-08-05-m5-crossscreen.md`
- M6 子进程架构重构计划（含真机结论回写）：`docs/superpowers/plans/2026-08-13-m6-child-process.md`
- M6 真机实施复盘（三条路线对比 + 方法论教训）：`docs/2026-08-18-M6子进程重构-真机复盘.md`
- M0 spike 结论与验收：`docs/superpowers/notes/m0-spike-results.md`
