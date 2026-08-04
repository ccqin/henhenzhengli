# M2 收纳盒/分组 + 双击隐藏 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 在 M1 的图标层之上加「收纳盒/分组」（Fences 式：把图标圈进可移动、可折叠、半透明的盒子）和「双击桌面空白隐藏/显示全部图标」，并修掉 M1 遗留的致命项 I-3（app 不启动时 HideIcons 残留 → 桌面空的自清理）。

**Architecture:** Core 加 `FenceConfig` 模型 + 持久化（复用 ConfigStore，存到 AppConfig.Fences）；App 加 `FenceControl`（WPF 用户控件，半透明可拖可折叠的盒子，承载归属图标）；IconLayerWindow 升级为承载「散落图标 + 多个 FenceControl」的画布；图标支持桌面↔收纳盒拖拽（更新归属与坐标）；双击画布空白切换图标可见性；图标右键菜单。I-3 用 RunOnce 注册表钩子：app 启动时写一条 RunOnce「下次登录时若 app 没启动则恢复 HideIcons=0」，app 正常启动后清除该钩子。

**Tech Stack:** C# / WPF / .NET 10 / xUnit / M1 已就绪的 IconLayerWindow + ConfigStore + RecoveryGuard。

## Global Constraints

- 沿用 M0/M1 项目分层与 TFM；Core 不依赖 WPF。
- 收纳盒坐标/尺寸/折叠态/标题持久化到 `AppConfig.Fences`（ConfigStore 已支持原子写 + 异常兜底）。
- 拖拽用 WPF 内置 Drag/Drop（DoDragDrop / Drop 事件），不引第三方。
- 双击空白隐藏不销毁图标（只切 Visibility），再双击恢复。
- 右键菜单操作必须与真实文件系统同步（删除 = 删 Desktop 文件，重命名 = 改文件名）。
- I-3 自清理：RunOnce 钩子路径 `HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce`，值 = 恢复 HideIcons=0 的小命令；app 正常运行时清除。
- 精确 `git add <paths>`；UI 任务用 spike + 真机验收，纯逻辑（Fence 模型/持久化/归属计算）用 TDD。

## M2 任务总览

| 执行单元 | 任务 | 性质 | 验收 |
|---|---|---|---|
| M2-T1 | FenceConfig 模型 + 持久化（ConfigStore 扩展） | TDD | 单测：Fence 列表 round-trip、增删改 |
| M2-T2 | FenceControl 控件（半透明/标题栏/折叠/整体拖动） | UI spike | 真机：盒子可拖、可折叠、标题可改 |
| M2-T3 | 图标拖入/拖出收纳盒（归属更新 + 坐标 + 持久化） | UI spike | 真机：图标在桌面↔盒子间拖动并落盘 |
| M2-T4 | 双击空白隐藏/显示全部图标 | UI | 真机：双击空白切换可见性 |
| M2-T5 | 图标右键菜单（打开/重命名/删除/打开文件位置） | UI | 真机：各项与文件系统同步 |
| M2-T6 | I-3 自清理（RunOnce 钩子）+ 收纳盒右键（新建/删除/重命名） | spike+代码 | 真机：app 不启动场景桌面图标能恢复 |
| M2-T7 | 接线（FenceControl 进 IconLayer + 持久化加载/保存）+ 冒烟 + tag | 集成 | 真机全流程 |

## 文件结构（M2 新增/改动）

```
src/DesktopManager.Core/
├── Models/
│   ├── AppConfig.cs            # 改：Fences 默认空（已是 init []）
│   └── FenceConfig.cs          # 新：record FenceConfig(Id, Title, X, Y, W, H, Folded, IconFilePaths)
├── Services/
│   └── ConfigStore.cs          # 已有（AppConfig.Fences 顺带持久化，无需改）
src/DesktopManager.App/
├── Windows/IconLayerWindow.xaml(.cs)  # 改：画布承载散落图标 + FenceControl；双击空白；右键
├── Controls/
│   └── FenceControl.xaml(.cs)  # 新：半透明可拖可折叠盒子控件
├── Services/IconExtractor.cs   # 已有
└── RecoveryGuard.cs            # 改：加 RunOnce 自清理钩子（SetSelfCleanupOnExit / ClearSelfCleanup）
src/DesktopManager.Tests/
└── FenceConfigTests.cs         # 新：Fence 持久化 round-trip
```

## 详细任务

### M2-T1 — FenceConfig 模型 + 持久化（TDD）

**Files:** Create `Core/Models/FenceConfig.cs`、`Tests/FenceConfigTests.cs`

- [x] FenceConfig：
```csharp
namespace DesktopManager.Core.Models;
public record FenceConfig(
    string Id,
    string Title,
    double X, double Y,
    double W, double H,
    bool Folded = false,
    IReadOnlyList<string> IconFilePaths = null!);
```
> 注意 CS1736：`IconFilePaths` 默认值用 `null!`（Load/使用处兜底空数组），或改 init-only 属性给 `= Array.Empty<string>()`（参考 AppConfig 的 M0 final fix 模式）。**统一用 init-only 属性**：
```csharp
public record FenceConfig
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
    public double W { get; init; } = 180;
    public double H { get; init; } = 120;
    public bool Folded { get; init; }
    public IReadOnlyList<string> IconFilePaths { get; init; } = Array.Empty<string>();
}
```
- [x] 测试：`Save` 一个含 Fence 的 AppConfig → `Load` → Fences 列表 round-trip（Title/X/Y/IconFilePaths 一致）；空 Fences 默认。ConfigStore 已支持（AppConfig.Fences 序列化），测试主要验证 FenceConfig 本身可序列化 + 默认值。
- [x] TDD 红→绿→commit。

### M2-T2 — FenceControl 控件（UI spike）

**Files:** Create `App/Controls/FenceControl.xaml(.cs)`

- [x] XAML：`UserControl`，根 `Border`（CornerRadius=6, Background=#80000000 半透明黑, BorderBrush=#40FFFFFF）。顶栏 `Grid`（标题 TextBlock + 折叠按钮 ▾）。内容区 `ItemsControl` 或 `WrapPanel` 放归属图标（折叠时 Collapsed）。
- [x] code-behind：
  - `Bind(FenceConfig)` 把 Title/坐标/折叠态绑上。
  - 顶栏 `MouseLeftButtonDown` → `DragMove()`（整体拖动）。
  - 折叠按钮 → 切内容区 Visibility + 更新 Folded。
  - 标题双击 → 进入编辑（TextBox）→ 回车确认改 Title。
- [x] 验收（真机）：盒子半透明显示、可整体拖、点折叠收起/展开、标题可改。
- [x] commit。

### M2-T3 — 图标拖入/拖出收纳盒（UI spike）

**Files:** Modify `IconLayerWindow.xaml.cs`、`FenceControl.xaml.cs`

- [x] 图标项（Image+TextBlock 的 StackPanel）设 `AllowDrop`/`MouseMove`+`DoDragDrop`（拖出），数据 = IconItem.FilePath。
- [x] FenceControl 内容区 `AllowDrop` + `Drop`：拖入则把 FilePath 加入该 Fence.IconFilePaths、从散落区移除；持久化。
- [x] 从 Fence 拖出到画布空白：反向操作。
- [x] 验收（真机）：图标在桌面↔盒子间拖动，重启 app 后归属保持（持久化生效）。
- [x] commit。

### M2-T4 — 双击空白隐藏/显示全部图标（UI）

**Files:** Modify `IconLayerWindow.xaml.cs`

- [x] Canvas（或根 Grid）`MouseLeftButtonDown`：若 `ClickCount>=2` 且 hit-test 命中的是画布本身（非图标/盒子）→ 切换一个 `_iconsVisible` bool，把散落图标和 FenceControl 的 Visibility 在 Visible/Collapsed 间切。
- [x] 验收（真机）：双击空白 → 所有图标+盒子隐藏（看壁纸）；再双击 → 恢复。
- [x] commit。

### M2-T5 — 图标右键菜单（UI）

**Files:** Modify `IconLayerWindow.xaml.cs`（或 IconItemControl 抽出）

- [x] 图标项加 `ContextMenu`：打开 / 重命名 / 删除 / 打开文件位置。
  - 打开 = `Process.Start(UseShellExecute)`（M1 已有 Open）。
  - 重命名 = 弹 InputBox（或内联 TextBox）→ `File.Move` 改 Desktop 文件名 → DesktopSync 自动同步。
  - 删除 = 确认 → `File.Delete`（或移到回收站，用 `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile` recycle）→ Sync 同步。
  - 打开文件位置 = `explorer.exe /select,"path"`。
- [x] 验收（真机）：四项可用且文件系统同步。
- [x] commit。

### M2-T6 — I-3 自清理（RunOnce 钩子）+ 收纳盒右键

**Files:** Modify `App/RecoveryGuard.cs`（或新 `Native/RunOnceSelfCleanup.cs`）

- [x] **I-3 自清理**：
  - app 启动时（TakeOver 之后）写 RunOnce：`HKCU\...\RunOnce\DM_RestoreIcons` = `"reg.exe add ...Advanced /v HideIcons /t REG_DWORD /d 0 /f"`（或指向一个恢复 helper）。
  - 含义：下次用户登录时，若 app 没启动，Windows 自动跑这条恢复 HideIcons=0（桌面图标回来）。
  - app 正常启动并接管后，**清除**该 RunOnce 值（避免每次登录都恢复）。
  - 这样：app 正常运行→无钩子；app 崩溃且不再启动→下次登录 RunOnce 恢复桌面。
- [x] 收纳盒右键：新建空 Fence / 删除 Fence / 重命名 Fence（操作 AppConfig.Fences + 持久化 + UI 刷新）。
- [x] 验收（真机）：写 RunOnce 后用 `reg query` 确认；模拟 app 不启动（注销/重启）→ 桌面图标恢复。
- [x] commit。

### M2-T7 — 接线 + 冒烟 + tag

**Files:** Modify `App/Windows/IconLayerWindow.xaml.cs`、`App/App.xaml.cs`

- [x] IconLayerWindow：启动时从 ConfigStore 加载 Fences → 创建 FenceControl 放画布 → 散落图标（不在任何 Fence 的 IconFilePaths 里的）正常显示。
- [x] Fence/图标变更（拖拽/重命名/删除/新建）→ 更新 AppConfig.Fences → ConfigStore.Save（防抖，避免频繁写）。
- [x] OnExit：保存当前布局 + 清 RunOnce（正常退出）+ RestoreExplorer。
- [x] build + test 全绿 → `git tag m2-fences`。
- [x] 真机冒烟：接管→图标+盒子显示→拖图标进盒子→双击空白隐藏→右键删除→重启 app 布局保持→退出恢复。

## 风险与对策

| 风险 | 对策 | 落地 |
|---|---|---|
| 拖拽体验粗糙（WPF Drag/Drop 默认观感差） | M2 先功能可用，观感 M2 末/M3 打磨 | T3 |
| 持久化频繁写（每次拖拽都 Save） | 防抖 Save（拖拽结束/500ms 后写） | T7 |
| I-3 RunOnce 在某些策略下被禁 | 备选：installer 卸载钩子（M6） | T6 |
| 重命名/删除文件触发 Sync 风暴 | Sync 已有 3s 对账兜底 + 幂等 Reconcile | 复用 M1 |
| 双击空白误触（点图标也隐藏） | hit-test 确认点的是画布空白才触发 | T4 |

## Self-Review

1. **Spec 覆盖**：grilling 定的「收纳盒/分组 + 双击隐藏」+ M1 遗留 I-3 全部有任务（T1-T7）✅。
2. **M1 衔接**：复用 IconLayerWindow、ConfigStore（原子写+兜底）、RecoveryGuard、DesktopSync。FenceConfig 走 init-only 属性（CS1736 教训）✅。
3. **未决（执行时定）**：删除走回收站 vs 永久删（默认回收站更安全）；重命名 UI 用 InputBox 还是内联；拖拽防抖时长。
4. **UI 任务**代码在执行时按 TDD-where-possible + spike 迭代（UI 预写全部 XAML 不现实，给设计 + 验收点）。
