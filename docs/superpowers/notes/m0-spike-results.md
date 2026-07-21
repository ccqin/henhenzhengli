# M0 Spike 结果与真机验收清单

M0 目标：搭好解决方案骨架 + 用 spike 验证三个最致命的技术未知（隐藏 explorer 桌面图标 / 显示器持久 ID / 置底点击穿透窗口），并跑通托盘常驻空壳。

## 代码层面（已验证：编译 0 error / 单测 3/3 通过）

| Spike | 文件 | 代码层面结论 |
|---|---|---|
| 隐藏/恢复 explorer 桌面图标 | `src/DesktopManager.Native/DesktopIconVisibility.cs` | 改注册表 `HideIcons` + 广播 `WM_SETTINGCHANGE/"Shell"`；写法正确，存注册表（explorer 重启后状态保持，符合 RecoveryGuard 设计） |
| 枚举显示器 | `src/DesktopManager.Native/MonitorEnumerator.cs` | `EnumDisplayMonitors`+`GetMonitorInfo` 正确；**已知 `szDevice` 不持久**（顺序变化会变），M3 换 `QueryDisplayConfig`/WMI `PNPDeviceID` |
| 置底+点击穿透窗口 | `src/DesktopManager.App/Windows/WallpaperWindow.xaml(.cs)` | `WS_EX_LAYERED\|TRANSPARENT\|NOACTIVATE` + `HWND_BOTTOM`；64 位安全的 `GetWindowLongPtr` 自适应 + 双条件错误判定 |
| 托盘常驻 | `src/DesktopManager.App/App.xaml(.cs)` | `H.NotifyIcon.Wpf`，`ShutdownMode=OnExplicitShutdown`，无 StartupUri，菜单退出 `Dispose+Shutdown` |

## 真机验收清单（请你在 Windows 真机跑一次确认）

### 1. 托盘常驻（可直接验证，无需改代码）
```bash
cd "d:/15.ai/狠狠整理"
dotnet run --project src/DesktopManager.App/DesktopManager.App.csproj
```
预期：任务栏右下角出现一个深蓝小图标；右键→「退出」→ 进程结束，无残留窗口。✅ 这条是 M0 唯一能直接运行的验收。

### 2-4. 三件套 spike（需临时挂调用验证）
M0.4/0.5/0.6 的 API 是库方法，还没接 UI。想现在验证，把下面**临时块**贴到 `src/DesktopManager.App/App.xaml.cs` 的 `OnStartup` 里 `base.OnStartup(e);` 之后（验证后**务必删掉**，不提交）：

```csharp
#if DEBUG
// --- M0 spike 临时验证，验证后删除 ---
// 2. 显示器枚举
var monitors = DesktopManager.Native.MonitorEnumerator.Enumerate();
var msg = string.Join("\n", monitors.Select(m => $"{m.DeviceName}: {m.Width}x{m.Height} @({m.X},{m.Y})"));
System.Windows.MessageBox.Show($"显示器数: {monitors.Count}\n{msg}", "M0.5");
// 3. 隐藏→恢复桌面图标
DesktopManager.Native.DesktopIconVisibility.HideDesktopIcons();
System.Windows.MessageBox.Show("桌面图标应已隐藏，点确定恢复", "M0.4");
DesktopManager.Native.DesktopIconVisibility.ShowDesktopIcons();
// 4. 置底+点击穿透窗口
var wp = new DesktopManager.App.Windows.WallpaperWindow();
wp.WindowState = System.Windows.WindowState.Maximized;
wp.Show();
#endif
```

预期现象：
- **M0.5**：弹框列出每个显示器设备名+分辨率+坐标；多屏机数量正确；记下 `\\.\DISPLAYn` 编号，**插拔/换顺序后再跑，编号会变**（证实 szDevice 不持久，M3 换方案）。
- **M0.4**：点确定前桌面图标全部消失；点确定后恢复。
- **M0.6**：深紫窗口全屏铺满主屏；点窗口任意处→点击穿透到下层（能选中下层）；打开任意普通窗口→永远在该窗口之上。

> 不想现在验证 2-4 也行——M1 接入 UI 时会自然调用这些 API，那时一并验证。M0 只要 1（托盘）通过即可推进。

## 已知 backlog（详见 `.superpowers/sdd/progress.md`）
ConfigStore 异常兜底、null! footgun、原子写、SendMessageTimeout 返回值、SendToBottom 语义、托盘 Dispose 兜底、MainWindow 死代码——全部 defer 到 M1/M6.3，最终 whole-branch review 统一 triage。

## 下一步
M0 骨架与三件套基底就绪 → 进入 **M1（桌面接管 + 图标镜像核心）**：RecoveryGuard、IconExtractor、读真实桌面文件夹、FileSystemWatcher+对账、自绘图标层窗口、explorer 重启监听。M1 详细 bite-sized 计划待编写。
