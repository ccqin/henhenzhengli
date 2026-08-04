# M3 多屏（图标层）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** 图标层从「单窗口铺主屏」升级为「每显示器一个 IconLayerWindow」：Fence/散落图标按**显示器持久 ID** 归属，布局每屏独立持久化；拔线/插回/换顺序/改分辨率/DPI 变化后布局不串屏、不丢失。另加**设置页屏幕排列配置**（Wallpaper Engine 式）：按比例显示各显示器矩形，拖拽调整排列并应用到 Windows 真实拓扑。真机环境：双显示器（已确认）。

**Architecture:**
- **Native** 加 `MonitorIdResolver`：`QueryDisplayConfig` 拿显示器目标设备路径（基于 EDID 硬件标识，插拔/换顺序不变），与 GDI 设备名（`\\.\DISPLAYn`，不持久）建立映射；`MonitorEnumerator` 扩展返回持久 ID + 工作区矩形 + 是否主屏。
- **Core** 加 `MonitorAssignment`（纯逻辑）：根据 config 里的 MonitorId + 当前在线显示器集合，算出「每个 path/Fence 归哪块屏」「哪块屏不在 → 孤儿（暂不显示）」「无归属记录 → 主屏」。配置模型 `FenceConfig`/`IconPosition` 加 `MonitorId` 字段（缺省空串 = 主屏，兼容旧 config）。
- **App** 加 `MultiMonitorHost`：枚举显示器 → 每屏一个 `IconLayerWindow`（定位到该屏工作区）→ 把 `DesktopSync` 的 diff 按归属分发给各窗口 → 聚合各窗口布局写回 ConfigStore。`IconLayerWindow` 改为接收「显示器信息 + 本屏 path 子集」，不再自己读 SystemParameters.WorkArea。
- **DisplayChangeWatcher**：监听 `WM_DISPLAYCHANGE`（防抖），触发重新枚举 → 窗口集 diff（关旧开新、移动/缩放存活的）→ 布局落盘再重建。
- **SettingsWindow（屏幕排列配置）**：Wallpaper Engine 式交互——按分辨率比例画出各显示器矩形（标持久 ID/主屏标记），拖拽改排列 + 边缘吸附，「应用」把新拓扑写回 Windows（`ChangeDisplaySettingsEx` 逐屏写 DM_POSITION）；应用后 `WM_DISPLAYCHANGE` 自动走 DisplayChangeWatcher 重建图标层，形成闭环。

**Tech Stack:** C# / WPF / .NET 10 / xUnit / QueryDisplayConfig P/Invoke / WM_DISPLAYCHANGE。

## Global Constraints

- 沿用 M0–M2 分层与 TFM；Core 不依赖 WPF/Win32（纯逻辑可单测），Native 仅 P/Invoke 封装。
- **禁用显示器索引/设备名做持久归属**（M0.5 已探明 `\\.\DISPLAYn` 不持久）——持久键只用设备路径；索引/设备名仅用于运行期定位窗口。
- **坐标系约定**：Fence/散落图标的 X/Y 一律是**所属屏工作区的本地坐标**（窗口左上角为原点），跨屏移动 = 换 MonitorId + 换算坐标。不引入全局虚拟桌面坐标，避免多 DPI 换算地狱。
- 旧 config 迁移：`MonitorId` 缺省（空串）→ 启动时归到**当前主屏**；一次保存后落盘为具体 ID（自然迁移，无专门迁移代码）。
- 拔掉的屏：其布局（Fence + 位置）完整保留在 config，只是不渲染；插回（持久 ID 匹配）→ 原位恢复。
- 桌面是单一逻辑空间：`SysListView32` 隐藏一次即覆盖所有屏（M2 机制不变）；桌面文件夹同步（DesktopSync）仍是**单一全局 watcher**，不按屏拆分。
- 无归属记录的新文件（桌面新建/外部拖入）→ 默认落**主屏**。
- 崩溃安全不变式保持：任何退出路径恢复原生图标（RunOnce + RecoveryGuard 不动）。
- 设置页改的是 **Windows 真实显示拓扑**（与 Wallpaper Engine 一致），不是 app 内虚拟布局；应用失败（驱动拒绝等）→ 错误提示，不改现状。
- 精确 `git add <paths>`；Core 纯逻辑 TDD，Native/App 用 spike + 双屏真机验收。

## M3 任务总览

| 执行单元 | 任务 | 性质 | 验收 | 依赖 |
|---|---|---|---|---|
| M3-T1 | MonitorIdResolver：QueryDisplayConfig 持久 ID + MonitorEnumerator 扩展 | Native spike | 双屏真机：换显示器顺序/插拔后持久 ID 不变，设备名可变 | — |
| M3-T2 | Core：MonitorId 进 config 模型 + MonitorAssignment 归属求解 | TDD | 单测：归属/孤儿/缺省主屏/旧 config 兼容 | — |
| M3-T3 | MultiMonitorHost + IconLayerWindow 每屏实例化 | 重构 | 双屏：各屏显示各自布局，互不串 | T1,T2 |
| M3-T4 | 聚合持久化（host 级 Save）+ 新图标默认主屏 | 重构 | 双屏：拖动/增删后 config 带正确 MonitorId，重启保持 | T3 |
| M3-T5 | 跨屏拖拽（图标/Fence 从一屏拖到另一屏） | UI spike | 双屏：拖过屏边界落另一屏，归属+坐标更新并落盘 | T4 |
| M3-T6 | DisplayChangeWatcher：热插拔/分辨率/DPI 响应 | spike+代码 | 双屏：拔线→该屏窗口关；插回→原位恢复；改分辨率→窗口重定位 | T3 |
| M3-T7 | 设置页：屏幕排列预览（只读：等比矩形 + 持久 ID/主屏标记 + 托盘入口） | UI | 双屏：预览与 Windows 实际拓扑一致 | T1 |
| M3-T8 | 设置页：拖拽重排 + 吸附 + 应用到 Windows 拓扑 | TDD(吸附)+spike(应用) | 双屏：拖拽换顺序→应用→系统拓扑真变了，图标层自动跟随重建 | T6,T7 |
| M3-T9 | 接线收尾 + 冒烟 + tag `m3-multimon` | 集成 | 双屏全流程冒烟 | T1–T8 |

## 文件结构（M3 新增/改动）

```
src/DesktopManager.Native/
├── MonitorEnumerator.cs        # 改：MonitorInfo 加 PersistentId / WorkX/Y/W/H / IsPrimary
├── MonitorIdResolver.cs        # 新：QueryDisplayConfig 设备路径 ↔ GDI 设备名映射
src/DesktopManager.Core/
├── Models/
│   ├── AppConfig.cs            # 改：FenceConfig/IconPosition 加 MonitorId（缺省 ""=主屏）
│   └── MonitorInfoCore.cs      # 新：record MonitorRef(string PersistentId, bool IsPrimary)（Core 侧纯数据，不依赖 Native）
├── Services/
│   └── MonitorAssignment.cs    # 新：path/Fence → MonitorId 归属求解（孤儿检测、缺省主屏）
src/DesktopManager.App/
├── MultiMonitorHost.cs         # 新：窗口集生命周期 + diff 分发 + 聚合 Save
├── DisplayChangeWatcher.cs     # 新：WM_DISPLAYCHANGE 监听（防抖）→ 事件
├── Windows/SettingsWindow.xaml(.cs)    # 新：屏幕排列配置页（画布 + 显示器矩形 + 拖拽 + 应用/重置按钮）
├── Windows/IconLayerWindow.xaml(.cs)  # 改：构造接收（显示器几何 + MonitorId + 本屏归属集）；去掉 SystemParameters.WorkArea
└── App.xaml.cs                 # 改：单 _iconLayer → host；sync.Changed → host.Dispatch(diff)；托盘加「设置」入口
src/DesktopManager.Core/
└── Services/ArrangementPlanner.cs     # 新：拖拽吸附/对齐纯逻辑（边缘吸附、网格贴合、越界钳制）
src/DesktopManager.Native/
└── DisplayTopologyApplier.cs   # 新：ChangeDisplaySettingsEx 逐屏写 DM_POSITION 提交新拓扑
src/DesktopManager.Tests/
├── MonitorAssignmentTests.cs   # 新
├── AppConfigMonitorIdTests.cs  # 新：MonitorId 序列化 round-trip + 旧 JSON 兼容（无字段→空串）
└── ArrangementPlannerTests.cs  # 新：吸附/对齐/钳制纯逻辑
```

## 详细任务

### M3-T1 — MonitorIdResolver：持久显示器 ID（Native spike）

> **落地偏差（真机已验收）**：GET_TARGET_NAME 在 Win11 24H2 + Intel MTL 上恒定 87（size/id/拓扑源扫描全排除），改用等价持久键 = GET_ADAPTER_NAME 的 PCI 硬件路径 + '#src' + source id（换排列顺序/重启稳定，验收通过）。

- [x] `MonitorIdResolver.cs`：`QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS)` 枚举 DISPLAYCONFIG_PATH_INFO；对每条 path：
  - `DisplayConfigGetDeviceInfo(DISPLAYCONFIG_SOURCE_DEVICE_NAME)` → GDI 设备名（`\\.\DISPLAYn`，对应 `EnumDisplayMonitors` 的 szDevice）
  - `DisplayConfigGetDeviceInfo(DISPLAYCONFIG_TARGET_DEVICE_NAME)` → `monitorDevicePath`（如 `\\?\DISPLAY#GSM5B08#4&...#{e6f07b5f-...}`，含 EDID 硬件 ID，**这就是持久 ID**）
  - 输出 `IReadOnlyDictionary<string gdiName, string persistentId>`
- [x] 失败兜底：QueryDisplayConfig 不可用/异常（RDP 会话、极老驱动）→ 退化为 GDI 设备名（宁可串屏也不崩，日志 Warning）。
- [x] `MonitorEnumerator` 扩展：`MonitorInfo` 加 `PersistentId`、工作区（`rcWork`：X/Y/W/H，排除任务栏）、`IsPrimary`（`MONITORINFOF_PRIMARY`）；`Enumerate()` 内部调 MonitorIdResolver 填 PersistentId。
- [x] 验收（双屏真机，可挂临时调试输出或写日志）：
  1. 当前顺序下枚举：两个显示器各有稳定 PersistentId（含 `DISPLAY#厂商#序列` 字样）、工作区正确、恰好一个 IsPrimary
  2. 系统设置里**交换显示器排列顺序**（1↔2）→ 重跑：PersistentId 与显示器一一对应不变，设备名可能互换
  3. 拔一根 HDMI/DP 再插回 → 重跑：该屏 PersistentId 不变
- [x] commit。

### M3-T2 — Core：config 加 MonitorId + MonitorAssignment（TDD）

- [x] `AppConfig.cs`：`FenceConfig` 与 `IconPosition` 各加 `string MonitorId { get; init; } = ""`（空串=主屏/未归属）。
- [x] 测试（先红）`AppConfigMonitorIdTests`：
  - 带 MonitorId 的 Fences/IconPositions Save→Load round-trip
  - **旧 JSON 兼容**：不含 MonitorId 字段的 JSON Load → MonitorId 为空串（System.Text.Json 缺字段走默认值，写测固化）
- [x] `MonitorAssignment.cs`（纯函数，输入：当前在线显示器集合 `IReadOnlyList<MonitorRef>`、config）：
  - `Resolve(string configMonitorId) → string?`：在线且匹配 → 该 ID；空串 → 主屏 ID；**不在线（拔掉）→ null（孤儿，不渲染）**
  - `FenceAssignments(config) → Dictionary<FenceId, string?>`、`LooseAssignments(iconPositions) → Dictionary<Path, string?>` 批量版
  - 无主屏的畸形拓扑（理论不发生）→ 全部孤儿，不抛
- [x] 测试（先红）`MonitorAssignmentTests`：匹配/缺省主屏/孤儿/无主屏/大小写一致（Ordinal）各 case。
- [x] TDD 红→绿→commit。

### M3-T3 — MultiMonitorHost + 每屏 IconLayerWindow（重构）

- [x] `IconLayerWindow` 构造签名改造：`IconLayerWindow(IConfigStore?, MonitorLayout layout, IReadOnlyList<FenceConfig> myFences, ...)` 或注入「显示器几何 + 本屏 Fence/位置子集」的等价参数对象；`SourceInitialized` 定位改用传入的工作区矩形（替代 `SystemParameters.WorkArea`）；`BuildAppConfigForSave` 输出项全部带上本屏 MonitorId。
  - 注意：每窗口独立 `IconExtractor` 共享方式不变（构造注入）；`_iconPositions`/`_fencedPaths` 语义不变，只是内容已经是本屏子集。
- [x] `MultiMonitorHost.cs`：
  - `Attach(...)`：`MonitorEnumerator.Enumerate()` → 每屏建窗口（主屏窗口接管 ShellRestartWatcher hwnd 与双击隐藏等全局无关行为——各窗口行为本就独立）
  - 持有 `Dictionary<string PersistentId, IconLayerWindow>`；`Windows` 只读暴露
  - 启动分配：`MonitorAssignment` 算 Fence/位置子集 → 各窗口 `ApplySnapshot` 前注入
- [x] `App.xaml.cs`：`_iconLayer` 字段替换为 `_host`；`_sync.Changed` → `_host.Dispatch(diff)`（下条实现）；OnExit `_host.SaveAllNow()` + 关窗口。
- [x] `Dispatch(DesktopDiff)` 首版（保守正确即可）：
  - **Added**：按归属（已有 Fence → Fence 的 MonitorId；无归属 → 主屏）投给单个窗口的 `ApplyDiff`（仅含该条的 diff）
  - **Removed**：广播给**所有**窗口（窗口内无此 path 则 reconcile 自然 no-op，避免归属查询竞态）
- [x] 验收（双屏真机）：两屏各出现一个图标层窗口、各占自己工作区（不盖任务栏）；config 里的 Fence 全部在主屏（旧 config 迁移正确）；散落图标在主屏；副屏空白画布可右键新建收纳盒（T4 落盘后才有持久意义，先看功能通）。
- [x] commit。

### M3-T4 — 聚合持久化 + 新图标默认主屏

- [x] Save 聚合：ConfigStore 收归 `MultiMonitorHost` 持有；各窗口 `SaveFencesDebounced` 改为通知 host（事件/回调），host 聚合**所有窗口**的 Fences + IconPositions（各自已带 MonitorId）→ 防抖 Save；`SaveAllNow` 供 OnExit。
  - 窗口内原 `_saveTimer` 逻辑上移到 host；窗口不再直接持 IConfigStore 写路径（读仍在启动注入）。
- [x] 新图标归属：`Dispatch` Added 无归属记录 → 主屏窗口 + 其 IconPositions 落盘时自动写主屏 MonitorId（T3 已做，这里补测试/日志确认）。
- [x] 验收（双屏）：副屏新建 Fence/拖个图标进副屏 Fence → 重启 app → 副屏内容原样；主屏内容不受影响；config.json 里各项 MonitorId 正确（手工看文件）。
- [x] commit。

### M3-T5 — 跨屏拖拽（UI spike）

- [x] 现有拖拽数据已含 `Text=FilePath`（app 内）与 FileDrop（外部），跨窗口 DoDragDrop WPF 原生支持（同一消息循环）。
- [x] 图标跨屏：A 屏散落图标拖到 B 屏画布空白 → B 屏 `IconCanvas_Drop` Text 分支：
  - 该 path 在 A 屏 `_looseIcons` → 从 A 移除（跨窗口协调经 host：host 提供 `MoveLoose(path, fromId, toId, dropPos)`）
  - 该 path 在 A 屏某 Fence → 出 Fence + 归 B 屏散落（dropPos 回填，复用 `_dropPosition` 机制）
  - 落盘：新 MonitorId + 新坐标（dropPos 已是 B 屏本地坐标，直接可用）
- [x] Fence 跨屏：FenceControl 拖出本窗口、落在另一屏画布 → host 迁移 FenceConfig（MonitorId 换 + X/Y 用 dropPos - Fence 尺寸偏移），源窗口移除、目标窗口 CreateFence + LoadIcons。
  - Fence 拖动目前是窗内 Thumb/Move；跨窗 Drop 需目标窗口画布 AcceptDrop Text/Fence 两种 payload（Fence 用 `Text="fence:<id>"` 前缀区分，或自定义 DataFormat——倾向前缀，零新依赖）。
- [x] 拖到**非图标层窗口**（如文件夹）语义不变：FileDrop → 文件移动（现有行为，FileDrop 分支不受影响）。
- [x] 验收（双屏）：图标/Fence 跨屏拖拽成功、落盘、重启保持；拖拽中途拔线不崩（Drop 目标窗口消失 → DoDragDrop 自然取消）。
- [x] commit。

### M3-T6 — DisplayChangeWatcher：热插拔/分辨率/DPI

- [x] `DisplayChangeWatcher.cs`：message-only 窗口（或挂主屏窗口 HwndSource hook）监听 `WM_DISPLAYCHANGE`；**500ms 防抖**（拓扑切换过程会连发多条）→ 事件 `DisplayChanged`。
- [x] host 响应（App 线程）：
  1. `SaveAllNow()`（先把现状落盘，防重建丢布局）
  2. 重新 `Enumerate()` → 与现有窗口集按 PersistentId diff：
     - 消失的屏 → 关窗口（布局已在 config，不删数据）
     - 新增的屏 → 建窗口 + 按 config 恢复该屏布局（插回原位恢复）
     - 存活的屏 → 仅移动/缩放到新工作区（`SetWindowPos`/WPF Left-Top-Width-Height；图标本地坐标**不换算**——工作区变小时超界项暂容忍，backlog 记一条）
  3. 主屏切换 → 无归属项的目标窗口可能变：重跑 Dispatch 的 Added 归属即可，存量不搬（可接受，记 backlog）
- [x] DPI：先 spike 现状（双屏不同缩放比，如 150%+100%）：窗口定位/图标尺寸是否正确（WPF 坐标是 DIP，Left/Top 按工作区 DIP 给即可）；若错位再引入每窗 `WM_DPICHANGED` 处理——**不在本任务默认范围内**，spike 结论写进 notes。
- [x] 验收（双屏）：拔副屏线 → 副屏窗口消失、主屏不受影响；插回 → 副屏布局原位恢复；改分辨率 → 窗口跟随工作区；整个过程无崩溃、config 不丢数据。
- [x] commit。

### M3-T7 — 设置页：屏幕排列预览（只读）

- [x] 托盘菜单加「设置」项 → 打开 `SettingsWindow`（普通激活窗口，非 NOACTIVATE，单实例：已开则激活前置）。
- [x] 画布区：按**分辨率等比**画出每个显示器矩形（统一缩放因子适配画布；位置 = Windows 拓扑坐标平移归一化，负坐标屏也能摆下）。
  - 矩形内标注：持久 ID 短名（厂商#序列尾段，全 ID 太长）+ 分辨率 + 主屏标记（★）；选中态高亮边框。
  - 数据源：`MonitorEnumerator.Enumerate()`（含 PersistentId/几何/主屏）。
- [x] 底部按钮区（本任务只放「刷新」；「应用/重置」T8 加）。
- [x] 验收（双屏）：预览与 Windows 显示设置里的排列**方向/相对位置一致**（含左屏在右、竖屏等异形拓扑）；拔插后点刷新预览同步。
- [x] commit。

### M3-T8 — 拖拽重排 + 吸附 + 应用到 Windows 拓扑

- [x] `ArrangementPlanner`（Core，TDD）：输入拖拽中矩形位置 + 其他矩形集合，输出吸附后位置：
  - 边缘吸附：与邻屏顶/底边对齐（阈值内自动贴齐，Windows 设置同款行为）；左右贴合（新矩形边贴现有矩形边，不留缝隙）
  - 重叠钳制：不允许与其他矩形重叠（重叠时沿拖拽轴推回到最近合法位）；连通性约束：新位置必须与至少一个现有矩形有边接触（Windows 要求显示拓扑连通）——违反时钳到最近合法位
- [x] 测试（先红）：顶/底对齐吸附、左右贴合、重叠推开、断连钳回、无邻屏时自由放置；阈值边界各 case。TDD 红→绿。
- [x] UI：矩形可拖（Thumb/DragDelta）实时走 Planner 吸附；拖动中半透明 + 吸附辅助线（可选，先不做）。
- [x] `DisplayTopologyApplier`（Native spike）：新拓扑提交：
  - 首选 legacy API（实现简单、文档充分）：逐屏 `ChangeDisplaySettingsEx(deviceName, DEVMODE{dmFields=DM_POSITION, dmPosition=新坐标}, CDS_UPDATEREGISTRY|CDS_NORESET)` → 最后 `ChangeDisplaySettingsEx(null, null, 0)` 一次性生效
  - 失败退化/备选：`SetDisplayConfig`（DISPLAYCONFIG 路径上改 source mode position）；legacy 不生效时 spike 切换
  - 返回值映射成友好错误（驱动拒绝/分辨率不兼容等）
- [x] 「应用」按钮：确认弹窗（提示屏幕会黑一下）→ Applier → 成功后等 `WM_DISPLAYCHANGE`（T6 watcher）自动重建图标层；「重置」= 重新枚举恢复当前真实拓扑预览（丢弃未应用拖拽）。
- [x] 验收（双屏）：拖拽把副屏换到主屏另一侧 → 应用 → **Windows 显示设置里拓扑真的变了**（鼠标跨屏方向随之变）→ 图标层自动跟随新拓扑重建（T6），两屏布局各自原位。
- [x] commit。

### M3-T9 — 接线收尾 + 冒烟 + tag

- [x] 清理：旧单窗口残留代码、`MainWindow.xaml`（若仍死代码）、过时注释。
- [x] build + test 全绿。
- [x] 双屏冒烟清单：
  1. 启动：两屏各自图标层，原生图标（两屏）全部隐藏
  2. 副屏 Fence 增删拖 + 跨屏拖拽 + 重启保持
  3. 拔线/插回/换排列顺序 → 布局不串屏（**换顺序是 M0.5 的致命风险点，必须测**）
  4. 设置页：预览与实际拓扑一致 → 拖拽换顺序 → 应用 → 系统拓扑变化 + 图标层自动重建
  5. 重启 explorer → 接管保持（双屏）
  6. 双击空白隐藏（每屏独立生效）
  7. 托盘退出 → 原生图标恢复、config 完整
- [x] `git tag m3-multimon` + README 里程碑表更新。

## 风险与对策

| 风险 | 严重度 | 对策 |
|---|---|---|
| QueryDisplayConfig 拿不到稳定路径（RDP/虚拟机/老驱动） | 🟠 高 | 退化 GDI 设备名 + Warning 日志；真机验收只要求物理双屏场景通过 |
| 换显示器顺序后串屏 | 🔴 致命 | 持久 ID 唯一归属键（T1 验收第 2 条专测）；索引只用于运行期 |
| 拓扑切换期间 WM_DISPLAYCHANGE 连发 → 重建风暴 | 🟠 高 | 500ms 防抖 + 重建前先 SaveAllNow |
| 多 DPI 坐标错位 | 🟡 中 | 每屏独立窗口 + 本地坐标系；T6 spike 现状，错位再补 DPICHANGED |
| 重构 IconLayerWindow 构造破坏 M2 行为 | 🟠 高 | T3 验收含 M2 回归（双击隐藏/右键/拖拽/持久化单屏照常）；单测保持全绿 |
| 跨窗口拖拽中途拓扑变化 | 🟡 中 | DoDragDrop 目标消失自然取消；Drop 前窗口已关 → host 判空 no-op |
| ChangeDisplaySettingsEx 提交失败/部分生效（驱动、缩放、刷新率限制） | 🟠 高 | 逐屏返回值检查，任一失败 → 全量回滚提示不改现状；legacy 不行 spike 切 SetDisplayConfig |
| 应用拓扑后图标层重建时序（WM_DISPLAYCHANGE 早于拓扑稳定） | 🟡 中 | T6 已有 500ms 防抖；应用按钮后额外等首个 WM_DISPLAYCHANGE 再验收 |

## Self-Review

- 与路线图 M3.1/M3.2/M3.3 对齐：T1=M3.1（QueryDisplayConfig），T3/T4/T5=M3.2（每屏窗口 + 归属，加了跨屏拖拽增量），T6=M3.3（热插拔/DPI）；T7/T8 是本次新增需求（Wallpaper Engine 式屏幕排列配置页），也为 M5 显示组 UI 打底（同一设置窗口后续加分组 tab）。
- 崩溃安全不变式：本里程碑不触碰 TakeOver/RunOnce/RecoveryGuard 主链路，仅 ShellRestartWatcher 挂载点从单窗口移到 host（挂主屏窗口 hwnd，行为不变）。
- 设置页改的是 Windows 真实拓扑（与 Wallpaper Engine 一致），不是 app 内虚拟布局——应用后由 T6 watcher 自动重建，不写 app 内拓扑副本（避免双真相源）。
- 坐标系统一为「屏内本地坐标」是刻意决策：避免全局虚拟坐标在混合 DPI 下的换算地狱；代价是超界项不自动归位（backlog）。
- 测试边界：Core（MonitorAssignment + config 兼容 + ArrangementPlanner）TDD；Native 持久 ID/拓扑应用与 App 多窗口行为 = 双屏真机验收（无法可靠自动化，遵循项目既定测试策略）。
