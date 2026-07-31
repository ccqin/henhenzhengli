# Layouter 借鉴分析（对照 DesktopManager M2）

> 参考项目：`例子/Layouter`（成熟 WPF 桌面图标布局管理工具，已发布级）
> 分析日期：2026-07-28。对照 DesktopManager M2（接管式 + FenceControl + ConfigStore）。

## 架构本质差异（先钉死，否则借鉴跑偏）

| 维度 | Layouter | DesktopManager M2 |
|---|---|---|
| 接管策略 | **不接管**，explorer 原生桌面照常 | **接管**，IconLayerWindow 替代原生 |
| 收纳原理 | **物理移动文件**到 `.layouterhidden` + `.meta` 记原路径 | **逻辑划分** `_fencedPaths`，文件不动 |
| 窗口结构 | 每分区=**独立 WPF Window**（owner=Progman/WorkerW 桌面级） | **单一全屏 Canvas** + FenceControl UserControl |
| 图标渲染 | `ItemsControl`+`ObservableCollection`+`DataTemplate`（数据驱动） | `SetIcons` 全量 `Clear`+重建（命令式） |
| 监听变化 | 无 FSW，SendMessage 让 Shell 自刷新 | FSW + 3s 对账 |
| 持久化 | 每分区一 JSON，`File.WriteAllText` **无原子写、无防抖** | ConfigStore 单文件 **原子写 + 防抖** |
| 日志 | Serilog 按天 rolling | `Debug.WriteLine` |
| 多屏 | 只单屏 | M3 待做 |

## 高价值借鉴（值得引入）

### P0 — 数据驱动渲染：ItemsControl + ObservableCollection + Canvas + DataTemplate
- **Layouter**：`ViewModels/DesktopManagerViewModel.cs`（ObservableCollection\<DesktopIcon\>）+ `Views/DesktopManagerWindow.xaml:119-171`（ItemsControl，ItemsPanel=Canvas IsItemsHost，ItemContainerStyle 绑 Canvas.Left/Top，ItemTemplate=Image+TextBlock+IValueConverter 取图标）。
- **对照**：我们 SetIcons 全量 Clear+重建，DesktopSync 触发即闪烁/丢拖拽状态。
- **收益**：WPF ItemContainerGenerator 在 ObservableCollection.Add/Remove 时天然只创建/销毁差异容器 = 免费增量 diff。一次解决痛点 1（全量重渲）+ 痛点 6（MVVM 可维护性）。
- **做法**：IconLayer 散落 Canvas + FenceControl ContentArea 都改 ItemsControl+ObservableCollection\<IconItem\>；IconItem 改 ObservableObject；增量操作 `_icons.Add/Remove` 替代 Children.Clear。
- **难度中，最高 ROI**。
- **避坑**：`Models/IconPartition.cs`（带 ArrangeIcons）是死代码未用上，别照抄。

### P1 — Serilog 按天 rolling 日志（30 行）
- **Layouter**：`Logs/LogConfig.cs`（37 行）+ App.xaml.cs Init/CloseAndFlush；csproj 三包（Serilog + Sinks.File + Extensions.Logging）；各处 `Log.Information(...)`。
- **对照**：我们全 Debug.WriteLine（M1 backlog）。
- **做法**：复制 LogConfig.cs + 3 PackageReference + 全局替换 Debug.WriteLine → Log.Information/Warning/Error。
- **避坑**：Layouter catch 块全用 Log.Information 记异常（错误降级），我们要正确分级。
- **难度低，半小时**。

### P2 — 图标失效按扩展名 fallback（缓解 Fence 残留）
- **Layouter**：`Utility/ShortcutUtil.cs:127-141` GetIconFromFile，文件不存在时用 `SHGetFileInfo(temp+ext, ..., SHGFI_USEFILEATTRIBUTES=0x10)` 按扩展名取通用图标。
- **对照**：痛点 3，Fence 内图标外部删/改名后残留空白。
- **做法**：IconExtractor 加 else 分支 SHGFI_USEFILEATTRIBUTES。缓解非根治（名字仍旧），根治仍需 FSW 监听 _fencedPaths。
- **难度低**。

### P2 — Fence snap 吸附 + 图标网格对齐
- **Layouter**：`Services/WindowManagerService.cs:267-453`（~200 行 snap 算法，SnapThreshold=10，LocationChanged/SizeChanged 检查边缘吸附）+ `DesktopManagerWindow.xaml.cs:340-383`（SnapWindowSizeToIconGrid 按图标 cell 整数倍回弹）。
- **做法**：FenceControl 拖拽 end 加 10px 工作区边缘吸附 + 图标网格吸附。
- **难度低-中**。

### P2 — IconExtractor 图片文件走 BitmapImage
- **Layouter**：`FilePathToIconConverter.cs:24-43`，图片文件（.png/.jpg）直接 BitmapImage + BitmapCacheOption.OnLoad，不持有文件句柄。
- **做法**：IconExtractor 加图片扩展名分支。

### P3 — 看产品诉求
- Shell IDList 拖放（此电脑/回收站等虚拟项进 Fence）：`Utility/ShellUtil.cs:111-200`，SHCreateShellItemArrayFromDataObject + PIDL。难度中。
- Shell 原生右键菜单（打开方式/发送到/属性全套）：`Utility/ShellContextMenu.cs`。难度中。
- 桌面级窗口 owner=WorkerW（替代 Topmost，抗 Win+D）：`Utility/SysUtil.cs:22-28`（SendMessage 0x052C + SetParent）。**M3 多屏前评估**，难度中高。

## 反向参考（Layouter 不如我们，避免成为它）

| 点 | Layouter 问题 | 我们现状（更优） |
|---|---|---|
| 图标缓存 | 无缓存，每次 binding 重提 SHGetFileInfo | IconExtractor 有缓存 → **加 LRU 上限即可** |
| 持久化 | File.WriteAllText 无原子无防抖，拖窗口触发十几次写 | ConfigStore 原子写+500ms 防抖+OnExit flush |
| 多屏 | 不支持（SystemParameters.WorkArea 单屏） | M3 待做（无帮助） |
| 插件系统 | Roslyn 编译加载 >2000 行+重依赖，自己都在废弃 | 不需要（过度设计） |
| 多窗口 ID 映射 | windowGuids/mapping 三层 + GetHashCode 当 key（脆弱） | FenceId(Guid) 挂 FenceConfig 更干净 |
| 物理移动隐藏 | File.Move 到 .layouterhidden（崩溃用户找不到） | 逻辑划分 _fencedPaths + I-3 --restore-icons 自清理 |

## 唯一可借鉴的小点（从反面教材里捡）
- 持久化分文件：Layouter 把样式（颜色/字体/透明度）和布局分文件（PartitionDataService vs PartitionSettingsService），支持全局样式+单分区自定义两层。**未来 Fence 支持主题切换时参考**，当前不需要。

## 行动建议
- M2 真机验收通过后，**最高 ROI = P0 数据驱动渲染重构**（解决全量重渲 + MVVM 两件事）。
- P1 Serilog 可独立先做（半小时，M1 backlog）。
- P2/P3 作为交互打磨/功能扩展按需。

## Layouter 关键文件索引
- 数据驱动：`ViewModels/DesktopManagerViewModel.cs`、`Views/DesktopManagerWindow.xaml:119-171`
- 日志：`Logs/LogConfig.cs`
- 桌面级窗口：`Utility/SysUtil.cs:22-28`（DesktopUtil 在外部 Win32.dll，源码不可见）
- 图标提取+fallback：`Utility/ShortcutUtil.cs:110-165`
- snap：`Services/WindowManagerService.cs:267-453`
- Shell 拖放：`Utility/ShellUtil.cs:111-200`
- 反面（避免）：`Utility/FilePathToIconConverter.cs`（无缓存）、`Services/PartitionDataService.cs`（无原子无防抖）、`Models/IconPartition.cs`（死代码）
