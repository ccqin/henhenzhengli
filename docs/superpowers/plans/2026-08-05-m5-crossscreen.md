# M5 跨屏壁纸（显示组）+ 设置窗口 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 「显示组」：选哪些屏一组，组内共享一个壁纸源——静态大图横跨多屏拼接正确（每窗渲染自己区域）；视频同源同步播（起点/循环对齐不漂移）。同时落地**设置窗口**（托盘入口）：屏幕排列预览 + 显示组管理 + Wallpaper Engine 式**拖拽重排并应用到 Windows 真实拓扑**（M3-T7/T8 欠账）。真机：双显示器。

**Architecture:**
- **Core**：`DisplayGroup` 模型（Id/Name/MonitorIds/Wallpaper{Kind,Path}）进 `AppConfig.DisplayGroups`；`CrossScreenLayout` 纯函数：组内显示器 rect（虚拟屏坐标）→ 虚拟画布（bounding box）→ 每屏裁剪 rect（图片像素坐标），可单测。`ArrangementPlanner` 纯函数：拖拽吸附/对齐/连通性钳制（M3-T8 欠账），可单测。
- **App**：`SettingsWindow`（普通激活窗口，单实例）：① 屏幕排列画布（等比矩形，可拖）+「应用排列」；② 显示组列表（建组/删组/勾选成员/设组壁纸）；③ 每屏独立壁纸的现有右键入口保留（成员屏让位组配置）。
- **壁纸渲染**：组成员屏的 `WallpaperWindow` 改为渲染组源：静态 = `TransformedBitmap`（源图缩放到画布）+ `CroppedBitmap`（本屏区域）；视频 = 每窗同源 MediaElement + `SyncedPlayback` 漂移校正（2s 轮询，>0.5s 对齐主窗 Position）。
- **拓扑应用**（M3-T8）：`DisplayTopologyApplier`（Native）：逐屏 `ChangeDisplaySettingsEx`（DM_POSITION，CDS_UPDATEREGISTRY|CDS_NORESET）→ 最后 `ChangeDisplaySettingsEx(null,null,0)` 一次性生效；成功后 `WM_DISPLAYCHANGE` 自动触发 M3 重建链路。
- **优先级**：成员屏的组壁纸 > 该屏独立壁纸（`ApplyWallpaperTo` 查组优先）。

**Tech Stack:** C# / WPF / .NET 10 / xUnit / ChangeDisplaySettingsEx / TransformedBitmap+CroppedBitmap / MediaElement Position 同步。

## Global Constraints

- 沿用 M0–M4 分层；Core 无 WPF/Win32（CrossScreenLayout/ArrangementPlanner 收纯数据）。
- 组配置与独立壁纸配置并存不互删：屏离开组时独立壁纸自动恢复（数据都在 config）。
- 单屏组/成员离线降级：组内在线屏 <2 → 按单屏铺满（不裁剪）；成员全离线 → 组配置保留（孤儿语义同 M3）。
- 拓扑变化（M3 重建）后组渲染自动重算（重建走 ApplyWallpaperTo，查组优先即可）。
- 拖拽排列只改**位置**（不改分辨率/主屏），MVP 语义与 Wallpaper Engine 对齐。
- 精确 `git add <paths>`；Core TDD；UI/拓扑应用真机验收（双屏）。

## M5 任务总览

| 执行单元 | 任务 | 性质 | 验收 | 依赖 |
|---|---|---|---|---|
| M5-T1 | DisplayGroup 模型 + CrossScreenLayout + ArrangementPlanner | TDD | 单测：config 兼容；画布/裁剪 rect 真值表；吸附/连通钳制 | — |
| M5-T2 | 设置窗口骨架 + 排列预览 + 显示组管理 UI | UI | 托盘开设置；矩形与真实拓扑一致；建组/删组/勾选/设组壁纸落盘 | T1 |
| M5-T3 | 跨屏静态大图渲染（每窗裁剪区域） | spike | 双屏一张大图无缝拼接（接缝对齐、不变形） | T1,T2 |
| M5-T4 | 跨屏视频同步（SyncedPlayback 漂移校正） | spike | 双屏视频同起点，5 分钟漂移 <0.5s（校正生效） | T3 |
| M5-T5 | 拖拽重排 + 应用到 Windows 拓扑（M3-T8 欠账） | TDD+spike | 设置里拖换顺序→应用→系统拓扑真变→M3 重建跟随 | T1,T2 |
| M5-T6 | 优先级/降级/重建联动 + 冒烟 + tag `m5-crossscreen` | 集成 | 成员屏组优先；拔屏降级单屏；插回恢复组渲染 | T1–T5 |

## 文件结构（M5 新增/改动）

```
src/DesktopManager.Core/
├── Models/AppConfig.cs              # 改：+ DisplayGroups: IReadOnlyList<DisplayGroup>；+ DisplayGroup record
├── Services/CrossScreenLayout.cs    # 新：虚拟画布 + 每屏裁剪 rect（纯函数）
├── Services/ArrangementPlanner.cs   # 新：拖拽吸附/对齐/连通钳制（纯函数，M3-T8 欠账）
src/DesktopManager.Native/
├── DisplayTopologyApplier.cs        # 新：ChangeDisplaySettingsEx 应用新排列
src/DesktopManager.App/
├── Windows/SettingsWindow.xaml(.cs) # 新：排列画布 + 显示组管理 + 应用排列
├── Media/SyncedPlayback.cs          # 新：组内视频漂移校正
├── MultiMonitorHost.cs              # 改：ApplyWallpaperTo 组优先；组渲染分发；重建联动
├── Windows/WallpaperWindow.xaml(.cs)# 改：SetGroupWallpaper（裁剪静态/同步视频）
└── App.xaml.cs                      # 改：托盘「设置」入口 + SettingsWindow 单实例
src/DesktopManager.Tests/
├── DisplayGroupTests.cs             # 新：config round-trip + 旧 JSON 兼容
├── CrossScreenLayoutTests.cs        # 新：画布/裁剪真值表
└── ArrangementPlannerTests.cs       # 新：吸附/连通钳制
```

## 详细任务

### M5-T1 — Core 模型 + 两个纯函数（TDD）

- [ ] `DisplayGroup`：record { Id="", Name="", MonitorIds=[], WallpaperKind=Image, WallpaperPath="" }；`AppConfig` + `DisplayGroups`（默认空，旧 JSON 兼容 null-coalesce）。
- [ ] `CrossScreenLayout`：
  - `Canvas(monitorRects) → (left,top,right,bottom)`：成员 rect 的 bounding box。
  - `CropRect(bitmapW, bitmapH, canvas, monitorRect, cover=true) → (x,y,w,h)` 像素坐标：源图按 **cover**（等比缩放铺满画布，超出裁掉，居中）映射到画布 → 本屏 rect 与画布交集 → 像素 rect。
  - 单屏/空组 → 返回整图（不裁剪）。
- [ ] `ArrangementPlanner`（M3-T8 欠账）：`Plan(dragged(rect), others[]) → rect`：边缘吸附阈值 24px（顶/底对齐 + 左右贴合）；重叠推开（沿拖拽主轴）；连通钳制（必须与某矩形边接触，违反钳到最近合法位）。
- [ ] 测试（先红）：`DisplayGroupTests`（round-trip + 旧 JSON）、`CrossScreenLayoutTests`（双屏左右/上下/单屏/大图 cover 居中/小图放大）、`ArrangementPlannerTests`（吸附/推开/连通/自由放置，阈值边界）。
- [ ] TDD 红→绿→commit。

### M5-T2 — 设置窗口骨架 + 显示组管理（UI）

- [ ] 托盘菜单加「设置…」→ `SettingsWindow` 单实例（已开则 Activate 前置）。普通窗口（可激活、可焦点、任务栏可见）。
- [ ] 排列预览区：`ItemsControl`/Canvas 画等比矩形（缩放因子适配 500x300 区域），矩形标注持久 ID 短名 + 分辨率 + 主屏 ★；数据源 `MonitorEnumerator.Enumerate()`；「刷新」按钮。
- [ ] 显示组管理区：组列表（ListBox）+「新建组」（勾选当前在线屏成员）+「删除组」+ 选中组编辑：成员 CheckBox 列表 +「设置组壁纸…」（OpenFileDialog）+「清除组壁纸」。
- [ ] 变更 → host API（`SetDisplayGroups(List<DisplayGroup>)`）→ 即时重渲染壁纸窗 + 防抖落盘（聚合 Save 带 DisplayGroups）。
- [ ] 验收（双屏）：建组（双屏）→ 设组壁纸 → 两屏即时切换为组渲染占位（T3 前按各自铺满）→ 重启保持；删组 → 回独立壁纸。
- [ ] commit。

### M5-T3 — 跨屏静态大图渲染（spike）

- [ ] `WallpaperWindow.SetGroupWallpaper(path, cropRectPixels?)`：crop=null → 现有整屏逻辑；否则 `BitmapImage` → `TransformedBitmap`（缩放使画布 DIP 尺寸映射像素）→ `CroppedBitmap(crop)` → Image.Source（Stretch=Fill，裁剪已精确）。
- [ ] host 分发：组成员屏 → 各自 CropRect（CrossScreenLayout）；组内在线屏 <2 → crop=null 降级。
- [ ] 接缝验证图：生成/下载一张带网格+中线的测试大图（如 3840x1080），双屏拼接网格连续无错位。
- [ ] 验收（双屏）：大图横跨无缝；改窗口大小（改分辨率）→ 重建后裁剪重算仍对齐。
- [ ] commit。

### M5-T4 — 跨屏视频同步（spike）

- [ ] `SyncedPlayback`：host 级，2s DispatcherTimer：组内视频窗取主窗（MonitorIds 序首在线屏）Position 为基准；其余窗 |Δ|>0.5s → `Position = master`（校正）；组窗同时起播（SetGroupWallpaper 时统一 Play）。
- [ ] WallpaperWindow 暴露 `VideoPosition` get/set（包装 MediaElement.Position）。
- [ ] 验收（双屏）：同视频双屏起播；手动制造漂移不可行 → 看日志 5 分钟无校正或校正次数 ≤1 且 |Δ| 收敛；肉眼画面同步。
- [ ] commit。

### M5-T5 — 拖拽重排 + 应用拓扑（M3-T8 欠账）

- [ ] 设置窗口排列区矩形可拖（Thumb/MouseCapture）：拖动实时走 `ArrangementPlanner.Plan`（吸附反馈：吸附时矩形边框高亮）。
- [ ] 「应用排列」按钮：确认弹窗（屏幕会黑一下）→ `DisplayTopologyApplier.Apply(newPositions: deviceId→(x,y))`：
  - Native：`EnumDisplayDevices`/QueryDisplayConfig 拿 GDI 设备名 → 逐屏 `ChangeDisplaySettingsEx(name, DEVMODE{dmFields=DM_POSITION, dmPosition}, CDS_UPDATEREGISTRY|CDS_NORESET)` → 收尾 `ChangeDisplaySettingsEx(null, null, 0)`；返回码映射友好错误（DISP_CHANGE_BADFLAGS 等）→ MessageBox，不改现状。
- [ ] 成功后 `WM_DISPLAYCHANGE` → M3 `RebuildToMatchTopology` 自动跟随（图标层/壁纸窗重定位）；设置窗口排列区刷新。
- [ ] 「重置」= 重枚举恢复真实拓扑预览（丢弃未应用拖拽）。
- [ ] 验收（双屏）：拖副屏到右侧 → 应用 → Windows 显示设置里排列真变 + 鼠标跨屏方向变 + 图标层/壁纸跟随；失败路径（拖到不连通位置被钳制）可见。
- [ ] commit。

### M5-T6 — 优先级/降级/重建联动 + 冒烟 + tag

- [ ] `ApplyWallpaperTo` 组优先：屏 ∈ 有壁纸的组 → 组渲染；否则独立壁纸；否则 Hidden。
- [ ] 拔组成员屏：组内在线 <2 → 剩余屏降级单屏铺满；插回 → 恢复组渲染（M3 重建走 ApplyWallpaperTo 自动）。
- [ ] build + test 全绿。
- [ ] 双屏冒烟：建组→设大图→拼接无缝→换视频→同步→拔插屏降级/恢复→拖拽换排列应用→托盘退出恢复原生桌面。
- [ ] `git tag m5-crossscreen` + README 里程碑表 + 账本更新。

## 风险与对策

| 风险 | 严重度 | 对策 |
|---|---|---|
| 跨屏拼接接缝错位（DPI/舍入） | 🟠 高 | 裁剪 rect 用整数像素 + 测试大图网格验收；混合 DPI 屏组 = backlog（M3 DPI spike 未做） |
| ChangeDisplaySettingsEx 应用失败/半生效 | 🟠 高 | 逐屏返回码检查 + 失败弹窗不改现状；CDS_NORESET 攒到最后一次性生效 |
| 视频双实例长时间漂移 | 🟡 中 | 2s 校正 >0.5s 对齐；校正本身跳变可接受（壁纸语义非观影） |
| 设置窗口与 M3 重建竞态（应用排列瞬间） | 🟡 中 | 应用后禁用按钮直至 WM_DISPLAYCHANGE 重建完成（host 事件回调） |
| 组配置与独立配置双真相 | 🟡 中 | 渲染优先级单点（ApplyWallpaperTo）；配置并存不互删，离开组自动恢复 |

## Self-Review

- 与路线图 M5.1/M5.2/M5.3 对齐：T2=M5.1（组+UI），T3=M5.2，T4=M5.3；M3-T7/T8 欠账并入 T2/T5。
- M6 接口预留：设置窗口后续可挂 MSIX/startupTask 设置页入口。
- 崩溃安全不变式不触碰：拓扑应用失败不接管不恢复，纯显示设置路径。
- 测试边界：Core（组 config/裁剪/吸附）TDD；渲染/同步/拓扑应用 = 双屏真机验收。
