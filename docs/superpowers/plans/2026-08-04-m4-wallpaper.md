# M4 壁纸层（单屏×每屏独立）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 每个显示器一个壁纸播放窗口（图标层之下、桌面之上）：静态图 + 视频/GIF 无声循环；全屏应用/锁屏/电池模式自动暂停省电；右键桌面即可换壁纸（持久化到 config，按屏归属）。真机：双显示器。

**Architecture:**
- **Core** 加 `WallpaperConfig`（MonitorId + Kind{Image,Video,Gif} + Path）进 `AppConfig.Wallpapers`；`PlaybackDecision` 纯决策函数（全屏/电池/锁屏 → 是否暂停），可单测。
- **App** 的 `WallpaperWindow` 从 M0.6 spike 升级为每屏实例：全屏（整屏 rect，含任务栏区）、点击穿透、置底且**明确位于本屏图标层之下**（`WindowInterop.PlaceBelow`，不靠创建顺序赌 Z-order）。内容按 Kind 渲染：`Image`（静态/GIF 帧）或 `MediaElement`（视频）。无壁纸时 `Visibility=Hidden`（不能透明遮系统壁纸——本窗口**不用 AllowsTransparency**，MediaElement 在透明窗内不渲染，WPF 硬约束）。
- **MultiMonitorHost** 兼管壁纸窗口集（与图标层 1:1 伴生）：拓扑重建时同步重建；壁纸配置是 host 级状态（窗口右键改 → 通知 host → 聚合 Save 带回）。
- **PlaybackGovernor**：前台窗口轮询（1.5s）+ `PowerModeChanged`/`SessionSwitch` 事件 → 决策 → 暂停/恢复各屏播放器。
- **设置入口**：图标层画布右键菜单「更换壁纸/移除壁纸」（per 该屏）→ OpenFileDialog → host 更新配置 + 窗口即时切换。

**Tech Stack:** C# / WPF / .NET 10 / xUnit / MediaElement（视频）/ GifBitmapDecoder（GIF 帧动画）/ GetSystemPowerStatus + SystemEvents（电源/会话）。

## Global Constraints

- 沿用 M0–M3 分层；Core 无 WPF/Win32 依赖（决策函数收纯数据）。
- 壁纸窗口**不开 AllowsTransparency**（MediaElement 不渲染 + 无壁纸时遮系统壁纸）；无壁纸 = `Visibility=Hidden`。
- 视频**无声循环**（MediaElement Volume=0 + MediaEnded 重播）；GIF 帧动画用 DispatcherTimer（~10fps 上限，MVP 够用，CPU 异常再优化）。
- Z-order 明确：图标层 `SendToBottom` 后壁纸窗口 `PlaceBelow(本屏图标层 hwnd)`——不用创建顺序赌。
- 壁纸归属键 = 持久 MonitorId（M3 契约）；无配置/屏离线同 M3 孤儿语义（config 保留，插回恢复）。
- 电源/锁屏暂停只停**播放**（视频 Pause / GIF 停表），窗口与布局不动。
- 精确 `git add <paths>`；Core TDD，App/Native spike + 双屏真机验收。

## M4 任务总览

| 执行单元 | 任务 | 性质 | 验收 | 依赖 |
|---|---|---|---|---|
| M4-T1 | WallpaperConfig 模型 + AppConfig.Wallpapers + PlaybackDecision | TDD | 单测：round-trip、旧 config 兼容、决策真值表 | — |
| M4-T2 | WallpaperWindow 每屏化 + 静态图 + Z-order 置底于图标层 | 重构+spike | 双屏：图片铺满整屏、在图标层下、点击穿透、无壁纸时不遮系统壁纸 | T1 |
| M4-T3 | 视频循环（MediaElement）+ GIF 帧动画 | spike | 双屏：mp4 无声循环、gif 播放、CPU 合理 | T2 |
| M4-T4 | PlaybackGovernor（全屏/电池/锁屏暂停） | TDD(决策)+spike(接线) | 双屏：开全屏视频/拔电源/Win+L → 壁纸停；恢复后继续 | T3 |
| M4-T5 | 右键设置入口 + 持久化 + 拓扑重建联动 | UI | 双屏：右键换图/视频/移除，重启保持，拔插屏壁纸随窗重建 | T2,T3 |
| M4-T6 | 收尾 + 冒烟 + tag `m4-wallpaper` | 集成 | 双屏全流程 | T1–T5 |

## 文件结构（M4 新增/改动）

```
src/DesktopManager.Core/
├── Models/AppConfig.cs             # 改：+ Wallpapers: IReadOnlyList<WallpaperConfig>；+ WallpaperConfig record + WallpaperKind enum
├── Services/PlaybackDecision.cs    # 新：ShouldPause(isFullScreenApp, onBattery, sessionLocked) 纯决策
src/DesktopManager.Native/
├── WindowInterop.cs                # 改：+ PlaceBelow(hwnd, belowHwnd)（SetWindowPos hwndInsertAfter 精确插层）
├── PowerStatus.cs                  # 新：GetSystemPowerStatus 封装（AC/电池）
src/DesktopManager.App/
├── Windows/WallpaperWindow.xaml(.cs)  # 改：每屏几何 + Image/MediaElement 内容 + GIF 帧动画 + Play/Pause
├── PlaybackGovernor.cs             # 新：前台轮询 + 电源/会话事件 → 决策 → 逐屏暂停/恢复
├── MultiMonitorHost.cs             # 改：壁纸窗口集伴生管理 + 壁纸配置聚合 Save + 右键换壁纸 API
├── Windows/IconLayerWindow.xaml.cs # 改：画布右键菜单 + 更换壁纸/移除壁纸
└── App.xaml.cs                     # 改：PlaybackGovernor 接线 + OnExit Dispose
src/DesktopManager.Tests/
├── WallpaperConfigTests.cs         # 新：round-trip + 旧 JSON 兼容
└── PlaybackDecisionTests.cs        # 新：决策真值表
```

## 详细任务

### M4-T1 — Core 模型 + 决策（TDD）

- [ ] `WallpaperConfig`：record { MonitorId="", Kind=Image, Path="" }；`WallpaperKind { Image, Video, Gif }`；`AppConfig` 加 `Wallpapers`（默认空）。
- [ ] 测试（先红）`WallpaperConfigTests`：round-trip（含 Kind 枚举序列化）、**旧 JSON 无 Wallpapers 字段 → 空列表**、默认值。
- [ ] `PlaybackDecision.ShouldPause(bool fullScreenApp, bool onBattery, bool locked) → bool`：locked → true；fullScreenApp → true；onBattery → true（MVP 三条件全暂停，粒度策略 backlog）。
- [ ] 测试（先红）`PlaybackDecisionTests`：8 行真值表全覆盖。
- [ ] TDD 红→绿→commit。

### M4-T2 — WallpaperWindow 每屏化 + 静态图（重构+spike）

- [ ] `WindowInterop.PlaceBelow(IntPtr hwnd, IntPtr belowHwnd)`：`SetWindowPos(hwnd, belowHwnd, ...SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE)`（hWndInsertAfter=belowHwnd → 本窗插到它正下方）。
- [ ] `WallpaperWindow` 构造改 `(MonitorInfo monitor)`：全屏 rect（monitor.X/Y/Width/Height，**非工作区**——壁纸盖任务栏后面）；`MakeClickThrough` + 置底；暴露 `RepositionTo(monitor)`（M3 同款）。
- [ ] 内容：`Image` 控件（Stretch=UniformToFill）；`SetWallpaper(WallpaperConfig?)`：null/空/文件不存在 → `Visibility=Hidden`；图片 → 加载显示 + `Visibility=Visible`。
- [ ] host 集成：`Attach`/`RebuildToMatchTopology` 里每屏图标层窗口 Show + SendToBottom 后，建/定位壁纸窗 → `PlaceBelow(壁纸hwnd, 图标层hwnd)`；壁纸配置从 `AppConfig.Wallpapers` 按 MonitorId 分配（孤儿语义同 M3）。
- [ ] 验收（双屏）：手动塞 config 一张图 → 两屏各自铺满（含任务栏区）、图标层在壁纸之上、点壁纸区穿透到桌面层行为正常、无壁纸的屏系统壁纸照常可见。
- [ ] commit。

### M4-T3 — 视频 + GIF（spike）

- [ ] 视频：`MediaElement`（LoadedBehavior=Manual, Volume=0, Stretch=UniformToFill）；`MediaEnded → Position=0 + Play()` 循环；`SetWallpaper` 按扩展名判 Kind（.mp4/.wmv/.avi→Video；.gif→Gif；其余→Image）——Kind 以文件实际为准，config 存的 Kind 只做提示（防用户改扩展名）。
- [ ] GIF：`GifBitmapDecoder` 读帧 + 帧元数据 Delay → `DispatcherTimer` 切 `Image.Source`；帧率钳制（delay<50ms 按 50ms，防空转）。
- [ ] `Pause()/Resume()`：视频 MediaElement.Pause/Play；GIF timer Stop/Start；`IsPlaying` 状态供 Governor 幂等。
- [ ] 验收（双屏）：mp4 无声循环无卡顿、gif 帧速正常、任务管理器看 CPU（视频 <5%、gif <3% 粗标，超了记 backlog）。
- [ ] commit。

### M4-T4 — PlaybackGovernor（TDD 决策 + spike 接线）

- [ ] `PowerStatus`（Native）：`GetSystemPowerStatus` → `IsOnBattery`。
- [ ] Governor（App）：DispatcherTimer 1.5s 轮询前台窗口（GetForegroundWindow + GetWindowRect + 类名过滤 Shell_TrayWnd/Progman/本 app 窗口）→ 覆盖整屏 = 全屏应用；订阅 `SystemEvents.PowerModeChanged`（电池变化即时响应）、`SessionSwitch`（锁屏/解锁）；状态变化 → `PlaybackDecision` → 逐屏 Pause/Resume。
- [ ] 决策部分 T1 已测；接线 spike：事件风暴防抖（PowerMode 连发）。
- [ ] 验收（双屏）：放全屏视频/游戏 → 壁纸视频停；Win+L 锁屏 → 停，解锁恢复；拔电源 → 停，插电恢复。
- [ ] commit。

### M4-T5 — 右键设置入口 + 持久化 + 重建联动

- [ ] 图标层画布右键菜单加「更换壁纸…」「移除壁纸」（挂现有 BuildCanvasContextMenu）→ 通知 host（窗口暴露事件或直接调 host API，带本窗 MonitorId）。
- [ ] host：`SetWallpaper(monitorId, path)`（OpenFileDialog 在窗口侧弹）→ 更新 `_wallpapers` 状态 + 目标窗 `SetWallpaper` + RequestSave；`RemoveWallpaper(monitorId)` 同理置空。
- [ ] `BuildAggregatedConfig` 带上 Wallpapers（host 状态 + 离线屏孤儿壁纸配置保留）。
- [ ] 拓扑重建：新屏按 config 恢复壁纸；离线屏壁纸进孤儿。
- [ ] 验收（双屏）：两屏各自右键换图/换视频/移除 → 即时生效 + 重启保持；拔副屏→插回 → 副屏壁纸随窗恢复。
- [ ] commit。

### M4-T6 — 收尾 + 冒烟 + tag

- [ ] build + test 全绿。
- [ ] 双屏冒烟：启动（无壁纸时系统壁纸正常）→ 两屏换图/视频 → 全屏/锁屏/电池暂停恢复 → 拔插屏壁纸跟随 → 重启布局+壁纸保持 → 托盘退出恢复原生桌面。
- [ ] `git tag m4-wallpaper` + README 里程碑表更新。

## 风险与对策

| 风险 | 严重度 | 对策 |
|---|---|---|
| MediaElement 在 AllowsTransparency 窗不渲染 | 🔴 致命（已规避） | 壁纸窗明确**不开**透明；无壁纸用 Hidden 不用透明 |
| 视频编码不支持（HEVC 等） | 🟠 高 | MediaEnded/Error 事件兜底（停播 + 日志），文档注明推荐 H.264 mp4 |
| 壁纸窗浮到图标层/文件夹上 | 🟠 高 | PlaceBelow 精确插层 + M3 的 Activated 守卫同款思路（NOACTIVATE 窗极少被激活） |
| GIF 大文件帧动画 CPU 高 | 🟡 中 | 帧率钳制 + 全屏/电池暂停（Governor 兜底）；仍高则 backlog 换解码方案 |
| 前台轮询误判全屏（无边框窗口/最大化） | 🟡 中 | rect 严格 ⊇ 屏幕 rect + 类名过滤；误判只导致暂停（安全方向） |
| 拓扑重建时壁纸窗口 Z-order 乱 | 🟡 中 | 重建统一走 PlaceBelow（不信任顺序），冒烟覆盖 |

## Self-Review

- 与路线图 M4.1/M4.2/M4.3 对齐：T2=M4.1，T3=M4.2，T4=M4.3。
- M5 接口预留：壁纸配置已按 MonitorId 独立，M5「显示组」= 组内屏共享同一 Path（Config 层加 GroupId 即可扩展，不改窗口模型）。
- 崩溃安全不变式不触碰：壁纸窗纯视觉层，无 explorer 接管路径。
- 测试边界：Core（config 兼容 + 决策真值表）TDD；窗口/播放器/Governor 接线 = 双屏真机验收。
