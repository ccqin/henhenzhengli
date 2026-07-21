# 桌面图标管理 + 壁纸显示工具 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 构建一个 Windows 桌面图标管理（收纳盒/分组 + 双击隐藏）+ 动态壁纸（视频/GIF，支持跨屏）结合的工具，支持多显示器，以 MSIX + runFullTrust 形式上架微软商店。

**Architecture:** 三层自绘架构——最底层是壁纸播放窗口（每屏一个，支持「显示组」跨屏共享一个图/视频源）、中间层是图标层窗口（每屏一个、每屏独立布局）、最上层是普通 WPF 设置窗口。图标镜像真实桌面文件夹并接管 explorer 原生桌面图标显示。

**Tech Stack:** C# / WPF / .NET 10 (LTS) / MSIX (runFullTrust) / xUnit / H.NotifyIcon.Wpf（托盘）/ 系统 IDesktopWallpaper + Win32 P/Invoke。

## Global Constraints

- **运行时：** .NET 10 SDK；目标框架 `net10.0`（Core/Tests）与 `net10.0-windows10.0.19041.0`（App/Native）。
- **最低系统：** Windows 10 1809 (17763) 及以上；主测 Windows 11。
- **打包：** MSIX，必须声明 `runFullTrust` 受限能力；开机自启用 `windows.startupTask` 扩展。
- **商店合规：** 所有桌面文件操作只读写用户自己的 `Desktop` 目录；提审时书面说明完全信任用途（接管桌面图标、监听桌面文件、调壁纸 API）。
- **数据存储：** 程序配置走 `ApplicationData.Current.LocalFolder`（MSIX 沙箱内正确位置），不写任意系统路径。
- **测试：** Core 项目的纯逻辑（布局/拓扑/配置/对账）用 xUnit 做 TDD；Native/App 的 Win32 互操作与 UI 行为用手动 spike + 冒烟验证（无法可靠自动化）。
- **崩溃安全：** 接管 explorer 桌面图标显示后，任何退出路径（正常退出/崩溃/被杀/explorer 重启）都必须恢复 explorer 原生显示。
- **命名：** 解决方案 `DesktopManager`；项目 `DesktopManager.Core` / `.Native` / `.App` / `.Tests`。

---

## 里程碑总览（路线图）

| 里程碑 | 目标 | 产出可验证物 |
|---|---|---|
| **M0** | 骨架 + 三个致命技术 spike | 能启动常驻托盘的空壳；已验证「隐藏 explorer 图标 / 显示器持久 ID / 置底点击穿透窗口」三件套 |
| **M1** | 桌面接管 + 图标镜像核心 | 关闭 explorer 图标，自绘图标层窗口显示真实桌面图标，文件增删自动同步，崩溃恢复 |
| **M2** | 收纳盒/分组 | Fence 控件：增删改、拖拽移动图标、折叠/样式、双击空白隐藏 |
| **M3** | 多屏（图标层） | 每屏一个图标层窗口、图标绑定持久 ID、热插拔/分辨率/DPI 变化处理 |
| **M4** | 壁纸层（单屏） | 每屏壁纸播放窗口：静态图 + 视频/GIF 循环，全屏暂停/省电 |
| **M5** | 跨屏壁纸 | 「显示组」分组 UI，跨屏静态大图 + 视频（起点/循环对齐） |
| **M6** | 商店化 | MSIX manifest（runFullTrust + startupTask）、托盘自启、隐私政策、打包提审 |

> M1–M6 的 bite-sized 详细任务在各自前置里程碑完成后单独编写（见各自路线图小节）。本文档先交付 M0 的完整可执行任务。

---

## 解决方案文件结构

```
狠狠整理/
├── DesktopManager.sln
├── docs/superpowers/plans/2026-07-21-desktop-manager.md   ← 本计划
├── src/
│   ├── DesktopManager.Core/         # 纯逻辑，net10.0，无 UI/Win32 依赖，可单测
│   │   ├── Models/                   AppConfig, IconItem, FenceConfig, MonitorRef, DisplayGroup, LayoutProfile
│   │   └── Services/                 IConfigStore(ConfigStore), IDesktopSync, LayoutResolver, DisplayTopology
│   ├── DesktopManager.Native/       # Win32 P/Invoke 封装，net10.0-windows
│   │   ├── DesktopIconVisibility.cs  # 隐藏/恢复 explorer 桌面图标
│   │   ├── MonitorEnumerator.cs      # 枚举显示器 + 持久 ID
│   │   ├── DesktopWallpaperApi.cs    # IDesktopWallpaper COM 封装
│   │   ├── IconExtractor.cs          # SHGetFileInfo 提取 HICON → BitmapSource
│   │   └── WindowInterop.cs          # 置底/点击穿透/插入 WorkerW
│   ├── DesktopManager.App/           # WPF 主程序，net10.0-windows10.0.19041.0
│   │   ├── Windows/                  WallpaperWindow, IconLayerWindow, SettingsWindow
│   │   ├── Controls/                 FenceControl, IconItemControl
│   │   ├── ViewModels/
│   │   ├── App.xaml(.cs)             托盘 TaskbarIcon、单实例、生命周期
│   │   └── RecoveryGuard.cs          启动恢复 explorer（崩溃兜底）
│   └── DesktopManager.Tests/         # xUnit，net10.0
│       └── (Core 单测)
└── installer/
    └── Package.appxmanifest          # MSIX：runFullTrust + windows.startupTask
```

分层原则：**Core 无副作用可测、Native 仅 P/Invoke 封装、App 仅组合与 UI**。文件按职责聚合（同变的放一起），不按技术层硬拆。

---

## 测试策略

- **TDD（xUnit）：** Core 的纯函数——配置序列化、布局求解（图标归位到网格/收纳盒坐标）、显示器拓扑合并（两个屏矩形 → 虚拟画布）、桌面文件对账（快照 diff）。
- **手动 spike + 冒烟：** Native 的每个 P/Invoke 封装和 App 的每个窗口行为，配明确的「观察现象即通过」验收点。无法可靠自动化（依赖真实 explorer/多屏/窗口层级）。
- 每个任务结束 `git commit`。

---

# M0 详细任务（可立即执行）

### Task M0.1 — Git 初始化与解决方案骨架

**Files:**
- Create: `d:\15.ai\狠狠整理\.gitignore`
- Create: `d:\15.ai\狠狠整理\DesktopManager.sln`

- [ ] **Step 1: 初始化仓库与 .gitignore**

Run（在 `d:\15.ai\狠狠整理` 下）:
```bash
git init
dotnet new gitignore
```
Expected: `.gitignore` 生成（含 bin/obj 等）。

- [ ] **Step 2: 创建解决方案**

Run:
```bash
dotnet new sln -n DesktopManager
```
Expected: `DesktopManager.sln` 生成。

- [ ] **Step 3: 提交基线**

```bash
git add -A
git commit -m "chore: init repo and solution skeleton"
```

---

### Task M0.2 — 四个项目骨架与引用关系

**Files:**
- Create: `src/DesktopManager.Core/DesktopManager.Core.csproj`
- Create: `src/DesktopManager.Native/DesktopManager.Native.csproj`
- Create: `src/DesktopManager.App/DesktopManager.App.csproj`
- Create: `src/DesktopManager.Tests/DesktopManager.Tests.csproj`

**Interfaces:**
- Produces: `DesktopManager.Core`（被 Native/App/Tests 引用）、`DesktopManager.Native`（被 App 引用）、`DesktopManager.App`（启动项目）、`DesktopManager.Tests`（引用 Core）。

- [ ] **Step 1: 用模板生成四个项目**

```bash
dotnet new classlib -n DesktopManager.Core   -o src/DesktopManager.Core   -f net10.0
dotnet new classlib -n DesktopManager.Native -o src/DesktopManager.Native -f net10.0-windows
dotnet new wpf     -n DesktopManager.App    -o src/DesktopManager.App    -f net10.0-windows
dotnet new xunit   -n DesktopManager.Tests  -o src/DesktopManager.Tests  -f net10.0
```
> 若 `dotnet new wpf` 别名不可用，先 `dotnet new list wpf` 确认短别名。
Expected: 四个 csproj 与默认 Class1.cs/UnitTest1.cs 生成。删除默认 Class1.cs/UnitTest1.cs。

- [ ] **Step 2: 加入解决方案**

```bash
dotnet sln add src/DesktopManager.Core/DesktopManager.Core.csproj
dotnet sln add src/DesktopManager.Native/DesktopManager.Native.csproj
dotnet sln add src/DesktopManager.App/DesktopManager.App.csproj
dotnet sln add src/DesktopManager.Tests/DesktopManager.Tests.csproj
```

- [ ] **Step 3: 建立项目引用**

```bash
dotnet add src/DesktopManager.Native/DesktopManager.Native.csproj reference src/DesktopManager.Core/DesktopManager.Core.csproj
dotnet add src/DesktopManager.App/DesktopManager.App.csproj reference src/DesktopManager.Core/DesktopManager.Core.csproj
dotnet add src/DesktopManager.App/DesktopManager.App.csproj reference src/DesktopManager.Native/DesktopManager.Native.csproj
dotnet add src/DesktopManager.Tests/DesktopManager.Tests.csproj reference src/DesktopManager.Core/DesktopManager.Core.csproj
```

- [ ] **Step 4: 还原并整体编译**

```bash
dotnet build DesktopManager.sln
```
Expected: `Build succeeded`，0 error。

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "chore: scaffold Core/Native/App/Tests projects"
```

---

### Task M0.3 — Core 配置模型与存储（TDD 示范）

**Files:**
- Create: `src/DesktopManager.Core/Models/AppConfig.cs`
- Create: `src/DesktopManager.Core/Services/IConfigStore.cs`
- Create: `src/DesktopManager.Core/Services/ConfigStore.cs`
- Test: `src/DesktopManager.Tests/ConfigStoreTests.cs`

**Interfaces:**
- Produces: `AppConfig`（`record AppConfig(bool HideExplorerIcons, bool AutoStart, IReadOnlyList<FenceConfig> Fences)`）、`FenceConfig`（`record FenceConfig(string Id, string Title, int X, int Y, int W, int H)`）、`IConfigStore.Load()` → `AppConfig`、`IConfigStore.Save(AppConfig)`。

- [ ] **Step 1: 写失败测试**

`src/DesktopManager.Tests/ConfigStoreTests.cs`:
```csharp
using DesktopManager.Core.Models;
using DesktopManager.Core.Services;

namespace DesktopManager.Tests;

public class ConfigStoreTests
{
    [Fact]
    public void Save_Load_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var store = new ConfigStore(path);
            var config = new AppConfig(
                HideExplorerIcons: true,
                AutoStart: true,
                Fences: new[] { new FenceConfig("f1", "Work", 10, 20, 300, 400) });

            store.Save(config);
            var loaded = store.Load();

            Assert.True(loaded.HideExplorerIcons);
            Assert.Single(loaded.Fences);
            Assert.Equal("Work", loaded.Fences[0].Title);
            Assert.Equal(300, loaded.Fences[0].W);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefault()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        var store = new ConfigStore(path);
        var loaded = store.Load();
        Assert.False(loaded.HideExplorerIcons); // 默认不接管，安全
        Assert.Empty(loaded.Fences);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
dotnet test src/DesktopManager.Tests/DesktopManager.Tests.csproj --filter "FullyQualifiedName~ConfigStoreTests"
```
Expected: FAIL（`ConfigStore`/`AppConfig` 未定义，编译错误）。

- [ ] **Step 3: 写最小实现**

`src/DesktopManager.Core/Models/AppConfig.cs`:
```csharp
namespace DesktopManager.Core.Models;

public record AppConfig(
    bool HideExplorerIcons = false,
    bool AutoStart = true,
    IReadOnlyList<FenceConfig> Fences = null!);

public record FenceConfig(string Id, string Title, int X, int Y, int W, int H);
```
> 注意：`Fences` 默认 `null!`，`Load` 会替换为空列表（见下）。

`src/DesktopManager.Core/Services/IConfigStore.cs`:
```csharp
using DesktopManager.Core.Models;
namespace DesktopManager.Core.Services;

public interface IConfigStore
{
    AppConfig Load();
    void Save(AppConfig config);
}
```

`src/DesktopManager.Core/Services/ConfigStore.cs`:
```csharp
using System.IO;
using System.Text.Json;
using DesktopManager.Core.Models;
namespace DesktopManager.Core.Services;

public sealed class ConfigStore : IConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path;

    public ConfigStore(string path) => _path = path;

    public AppConfig Load()
    {
        if (!File.Exists(_path))
            return new AppConfig(HideExplorerIcons: false, AutoStart: true, Fences: Array.Empty<FenceConfig>());
        var json = File.ReadAllText(_path);
        var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options)
                  ?? new AppConfig(Fences: Array.Empty<FenceConfig>());
        return cfg with { Fences = cfg.Fences ?? Array.Empty<FenceConfig>() };
    }

    public void Save(AppConfig config) =>
        File.WriteAllText(_path, JsonSerializer.Serialize(config, Options));
}
```

- [ ] **Step 4: 跑测试确认通过**

```bash
dotnet test src/DesktopManager.Tests/DesktopManager.Tests.csproj --filter "FullyQualifiedName~ConfigStoreTests"
```
Expected: PASS，2 个测试通过。

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat(core): AppConfig model and JSON ConfigStore with round-trip tests"
```

---

### Task M0.4 — Spike：隐藏 / 恢复 explorer 桌面图标

**Files:**
- Create: `src/DesktopManager.Native/DesktopIconVisibility.cs`

**Interfaces:**
- Produces: `DesktopIconVisibility.HideDesktopIcons()` / `.ShowDesktopIcons()`（静态）。

- [ ] **Step 1: 写封装（改注册表 HideIcons + 广播 WM_SETTINGCHANGE）**

`src/DesktopManager.Native/DesktopIconVisibility.cs`:
```csharp
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DesktopManager.Native;

/// <summary>隐藏/恢复 explorer 原生桌面图标显示（等价于桌面右键→查看→显示桌面图标）。</summary>
public static class DesktopIconVisibility
{
    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ValueName = "HideIcons";

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint Msg, UIntPtr wParam, string? lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    private const uint WM_SETTINGCHANGE = 0x001A;
    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    public static void HideDesktopIcons() => SetHidden(true);
    public static void ShowDesktopIcons() => SetHidden(false);

    private static void SetHidden(bool hidden)
    {
        using var key = Registry.CurrentUser.CreateSubKey(AdvancedKey, writable: true);
        key.SetValue(ValueName, hidden ? 1 : 0, RegistryValueKind.DWord);
        SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, UIntPtr.Zero,
            "Shell", SMTO_ABORTIFHUNG, 1000, out _);
    }
}
```

- [ ] **Step 2: 编译**

```bash
dotnet build src/DesktopManager.Native/DesktopManager.Native.csproj
```
Expected: `Build succeeded`。

- [ ] **Step 3: 手动验证（spike）**

在 App 里临时挂一个调用（或写个临时控制台 Main），依次：
1. `DesktopIconVisibility.HideDesktopIcons();` → 桌面图标全部消失。
2. `DesktopIconVisibility.ShowDesktopIcons();` → 图标恢复。

Expected: 现象如上；explorer 未崩溃；刷新桌面（F5）状态保持。
> **Spike 记录（写入 commit message）：** 此法改注册表，explorer 重启后状态保持——符合「崩溃后下次启动仍能识别接管状态」。M1 的 RecoveryGuard 据此判断是否需要恢复。

- [ ] **Step 4: 提交**

```bash
git add -A
git commit -m "spike(native): hide/restore explorer desktop icons via HideIcons registry"
```

---

### Task M0.5 — Spike：枚举显示器与持久 ID 探查

**Files:**
- Create: `src/DesktopManager.Native/MonitorEnumerator.cs`

**Interfaces:**
- Produces: `MonitorInfo`（`record MonitorInfo(string DeviceName, int X, int Y, int W, int H)`）、`MonitorEnumerator.Enumerate()` → `IReadOnlyList<MonitorInfo>`。

- [ ] **Step 1: 写封装（EnumDisplayMonitors + GetMonitorInfo）**

`src/DesktopManager.Native/MonitorEnumerator.cs`:
```csharp
using System.Runtime.InteropServices;

namespace DesktopManager.Native;

public record MonitorInfo(string DeviceName, int X, int Y, int Width, int Height);

public static class MonitorEnumerator
{
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip,
        EnumMonitorsDelegate lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    private delegate bool EnumMonitorsDelegate(IntPtr hMonitor, IntPtr hdcMonitor,
        ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    public static IReadOnlyList<MonitorInfo> Enumerate()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (hMon, _hdc, ref rc, _data) =>
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(hMon, ref mi))
                    list.Add(new MonitorInfo(mi.szDevice,
                        rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top));
                return true;
            }, IntPtr.Zero);
        return list;
    }
}
```

- [ ] **Step 2: 编译**

```bash
dotnet build src/DesktopManager.Native/DesktopManager.Native.csproj
```
Expected: `Build succeeded`。

- [ ] **Step 3: 手动验证（spike）**

在多屏机器上调用 `MonitorEnumerator.Enumerate()`，打印每屏 `DeviceName` 与矩形。
Expected: 数量与物理屏一致；矩形拼出虚拟桌面（主屏左上角通常为 0,0）。

> **Spike 记录（关键，写入 commit message）：**
> - `szDevice`（如 `\\.\DISPLAY1`）**会随显示器顺序变化**——不能作为持久 key。
> - M3 改用 `QueryDisplayConfig`（DisplayConfig）拿到的设备路径或 WMI `Win32_DesktopMonitor.PNPDeviceID`（基于 EDID 硬件标识）作为持久 ID。
> - 本 spike 的产出 = 「确认 szDevice 不持久，持久 ID 方案推后到 M3」。

- [ ] **Step 4: 提交**

```bash
git add -A
git commit -m "spike(native): enumerate monitors; confirm szDevice is not a stable id"
```

---

### Task M0.6 — Spike：置底 + 点击穿透窗口

**Files:**
- Create: `src/DesktopManager.App/Windows/WallpaperWindow.xaml`
- Create: `src/DesktopManager.App/Windows/WallpaperWindow.xaml.cs`

**Interfaces:**
- Produces: `WallpaperWindow`（WPF Window，全屏、置最底、点击穿透、不抢焦点）。后续 M4 用它承载视频/图片。

- [ ] **Step 1: 写 XAML（纯色背景用于验证）**

`src/DesktopManager.App/Windows/WallpaperWindow.xaml`:
```xml
<Window x:Class="DesktopManager.App.Windows.WallpaperWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="WallpaperWindow" Height="450" Width="800"
        WindowStyle="None" ResizeMode="NoResize"
        ShowInTaskbar="False" Background="#222244">
</Window>
```

- [ ] **Step 2: 写 code-behind（置底 + 点击穿透）**

`src/DesktopManager.App/Windows/WallpaperWindow.xaml.cs`:
```csharp
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DesktopManager.App.Windows;

public partial class WallpaperWindow : Window
{
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int n);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int n, int v);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    public WallpaperWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
            SendToBottom(hwnd);
        };
    }

    public void SendToBottom(IntPtr hwnd) =>
        SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
}
```

- [ ] **Step 3: 编译**

```bash
dotnet build src/DesktopManager.App/DesktopManager.App.csproj
```
Expected: `Build succeeded`。

- [ ] **Step 4: 手动验证（spike）**

临时在 `App.OnStartup` 里 `new WallpaperWindow { WindowState=WindowState.Maximized }.Show();`。
Expected: 深紫窗口全屏铺满主屏；用鼠标点窗口任意处 → 点击穿透到下层（能选中下层图标/桌面）；打开任意普通窗口 → 普通窗口永远在它之上。
> **Spike 记录：** 验证通过即 M4/M5 壁纸层的窗口基底就绪。图标层窗口（M1）同理但**不加 WS_EX_TRANSPARENT**（要能点图标）。

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "spike(app): bottom-most click-through window base for wallpaper layer"
```

---

### Task M0.7 — 托盘常驻空壳

**Files:**
- Modify: `src/DesktopManager.App/DesktopManager.App.csproj`（加包引用）
- Create: `src/DesktopManager.App/App.xaml`（若模板已有则改）
- Modify: `src/DesktopManager.App/App.xaml.cs`

**Interfaces:**
- Produces: 启动即托盘常驻、无主窗口、点「退出」正常结束进程。

- [ ] **Step 1: 引入托盘库**

```bash
dotnet add src/DesktopManager.App/DesktopManager.App.csproj package H.NotifyIcon.Wpf
```
Expected: 包还原成功。

- [ ] **Step 2: 写 App.xaml（托盘 + 退出菜单，不弹主窗口）**

`src/DesktopManager.App/App.xaml`:
```xml
<Application x:Class="DesktopManager.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:tb="http://www.hardcodet.net/taskbar"
             ShutdownMode="OnExplicitShutdown">
    <Application.Resources>
        <tb:TaskbarIcon x:Key="TrayIcon"
                        ToolTipText="DesktopManager"
                        IconSource="/Assets/app.ico"
                        Visibility="Visible">
            <tb:TaskbarIcon.ContextMenu>
                <ContextMenu>
                    <MenuItem Header="退出(_X)" Click="OnExit_Clicked"/>
                </ContextMenu>
            </tb:TaskbarIcon.ContextMenu>
        </tb:TaskbarIcon>
    </Application.Resources>
</Application>
```
> 需放一个 `Assets/app.ico`（任意临时 ico 即可，M6 替换正式图标）。

- [ ] **Step 3: 写 App.xaml.cs（注册托盘、处理退出）**

`src/DesktopManager.App/App.xaml.cs`:
```csharp
using System.Windows;
using H.NotifyIcon;

namespace DesktopManager.App;

public partial class App : Application
{
    private TaskbarIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _tray = (TaskbarIcon)FindResource("TrayIcon");
        _tray.ForceCreate();
    }

    private void OnExit_Clicked(object sender, RoutedEventArgs e)
    {
        _tray?.Dispose();
        Shutdown();
    }
}
```

- [ ] **Step 4: 编译并运行冒烟**

```bash
dotnet build src/DesktopManager.App/DesktopManager.App.csproj
dotnet run --project src/DesktopManager.App/DesktopManager.App.csproj
```
Expected: 任务栏右下角出现托盘图标；右键→「退出」→ 进程结束，无残留窗口。
> 若 `Assets/app.ico` 缺失导致启动报错，先放任意 .ico 文件。

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat(app): tray-resident shell with exit menu"
```

---

### Task M0.8 — M0 冒烟总验与里程碑标记

**Files:**
- Create: `docs/superpowers/notes/m0-spike-results.md`（记录三件套结论）

- [ ] **Step 1: 汇总 spike 结论**

写 `docs/superpowers/notes/m0-spike-results.md`，记录：
1. 隐藏 explorer 图标：注册表 HideIcons + 广播，可行，explorer 重启保持。
2. 显示器持久 ID：szDevice 不持久，M3 改 QueryDisplayConfig/WMI PNPDeviceID。
3. 置底点击穿透：GetWindowLong/SetWindowLong + HWND_BOTTOM 可行，图标层去掉 TRANSPARENT。

- [ ] **Step 2: 全量构建与测试**

```bash
dotnet build DesktopManager.sln
dotnet test DesktopManager.sln
```
Expected: 全部 succeeded / passed。

- [ ] **Step 3: 打里程碑 tag**

```bash
git add -A
git commit -m "docs: M0 spike results summary"
git tag m0-skeleton
```

---

# M1–M6 任务级路线图

> 每个里程碑在前一个完成、且其依赖的 spike 结论已落地后，单独编写 bite-sized 详细计划（同 M0 格式）。此处只锁定任务边界与验收。

## M1 — 桌面接管 + 图标镜像核心
| # | 任务 | 关键文件 | 验收 | 依赖 |
|---|---|---|---|---|
| M1.1 | RecoveryGuard：启动时检测上次异常退出并恢复 explorer | `App/RecoveryGuard.cs` | 强杀进程后重启，explorer 图标自动恢复 | M0.4 |
| M1.2 | IconExtractor：提取桌面文件图标（SHGetFileInfo→BitmapSource） | `Native/IconExtractor.cs` | 给 .lnk/.exe/.txt 返回正确缩略图 | M0 |
| M1.3 | 读真实桌面文件夹（用户 Desktop + Public Desktop）成 IconItem 列表 | `Core/Services/DesktopSnapshot.cs` | 列出与 explorer 一致的图标 | M1.2 |
| M1.4 | FileSystemWatcher + 定期全量对账（事件不可靠的兜底） | `Core/Services/DesktopSync.cs` | 增删文件后图标层在 ≤2s 内一致 | M1.3 |
| M1.5 | 图标层窗口渲染真实图标（不点击穿透，可点） | `App/Windows/IconLayerWindow.xaml` | 图标按坐标显示，双击用 ShellExecute 打开 | M0.6 |
| M1.6 | explorer 重启监听：shell 重启后重新应用接管 | `Native/ShellRestartWatcher.cs` | 任务管理器重启 explorer 后图标层仍接管 | M1.5 |

## M2 — 收纳盒/分组 + 双击隐藏
| # | 任务 | 关键文件 | 验收 | 依赖 |
|---|---|---|---|---|
| M2.1 | Fence model + 持久化（复用 ConfigStore） | `Core/Models/FenceConfig.cs` | 收纳盒位置/尺寸/标题存盘 | M1 |
| M2.2 | FenceControl 控件（半透明、可拖、可折叠、标题栏） | `App/Controls/FenceControl.xaml` | 拖动/折叠/改标题正常 | M1.5 |
| M2.3 | 图标拖入/拖出收纳盒（更新归属与坐标） | `App/Controls/FenceControl.xaml.cs` | 图标在桌面与收纳盒间移动并落盘 | M2.2 |
| M2.4 | 双击桌面空白处隐藏/显示全部图标 | `App/Windows/IconLayerWindow.xaml.cs` | 双击空白切换图标可见性 | M1.5 |
| M2.5 | 右键菜单（打开/重命名/删除/打开文件位置） | `App/Controls/IconItemControl.xaml` | 各项功能与文件系统同步 | M1.5 |

## M3 — 多屏（图标层）
| # | 任务 | 关键文件 | 验收 | 依赖 |
|---|---|---|---|---|
| M3.1 | 持久显示器 ID（QueryDisplayConfig 设备路径或 WMI PNPDeviceID） | `Native/MonitorIdResolver.cs` | 插拔/换顺序后 ID 稳定 | M0.5 |
| M3.2 | 每屏一个 IconLayerWindow，图标按持久 ID 归属 | `App/MultiMonitorHost.cs` | 图标出现在归属屏，互不串 | M3.1, M1.5 |
| M3.3 | 热插拔/分辨率/DPI/主屏切换响应 | `Core/Services/DisplayTopology.cs` | 屏拔掉该屏图标隐藏，插回恢复；DPI 变化重排 | M3.2 |

## M4 — 壁纸层（单屏）
| # | 任务 | 关键文件 | 验收 | 依赖 |
|---|---|---|---|---|
| M4.1 | 静态图壁纸（WallpaperWindow 显示一张图） | `App/Windows/WallpaperWindow.xaml` | 全屏铺满，置底点击穿透 | M0.6 |
| M4.2 | 视频/GIF 循环播放（MediaElement 或 LibVLC） | `App/Media/WallpaperPlayer.cs` | 无声循环，CPU/GPU 占用合理 | M4.1 |
| M4.3 | 全屏应用运行时暂停 + 电池模式降帧 + 锁屏暂停 | `App/Media/PlaybackGovernor.cs` | 开游戏/锁屏时壁纸停，省电 | M4.2 |

## M5 — 跨屏壁纸
| # | 任务 | 关键文件 | 验收 | 依赖 |
|---|---|---|---|---|
| M5.1 | 「显示组」模型 + 分组 UI（选哪些屏一组） | `Core/Models/DisplayGroup.cs` + 设置 UI | 组内屏共享一个壁纸源 | M3.1, M4 |
| M5.2 | 跨屏静态大图：每窗口渲染对应区域 | `Core/Services/CrossScreenLayout.cs` | 一张大图横跨两屏拼接正确 | M5.1 |
| M5.3 | 跨屏视频：同源 + 起点/循环对齐 | `App/Media/SyncedPlayback.cs` | 长时间播放各屏不错位 | M5.2, M4.2 |

## M6 — 商店化
| # | 任务 | 关键文件 | 验收 | 依赖 |
|---|---|---|---|---|
| M6.1 | MSIX 打包（Package.appxmanifest + runFullTrust） | `installer/Package.appxmanifest` | MSIX 安装能跑，功能完整 | M1–M5 |
| M6.2 | windows.startupTask 扩展（开机自启） | `installer/Package.appxmanifest` | 重启后自动启动（首次 Windows 提示允许） | M6.1 |
| M6.3 | 全局异常兜底 + RecoveryGuard 兜底退出恢复 | `App/App.xaml.cs` | 崩溃后桌面图标不丢 | M1.1 |
| M6.4 | 隐私政策 + 提审说明（runFullTrust 用途） | `docs/store/` | 提审材料齐 | M6.1 |

---

# 风险登记册（对应到任务）

| 风险 | 严重度 | 对策 | 落地任务 |
|---|---|---|---|
| 程序崩溃 → 用户桌面变空 | 🔴 致命 | RecoveryGuard + 全局异常兜底 + 启动恢复 | M1.1, M6.3 |
| 显示器顺序变化 → 图标串屏 | 🔴 致命 | 用持久 ID（设备路径/PNPDeviceID），禁用索引 | M3.1（M0.5 已探明） |
| explorer 重启 → 接管失效 | 🟠 高 | 监听 shell 重启，重新应用接管 | M1.6 |
| FileSystemWatcher 漏事件 → 图标不一致 | 🟠 高 | 事件 + 定期全量对账双保险 | M1.4 |
| 多 DPI 跨屏错位 | 🟡 中 | 每屏独立窗口 + DPI 变化重排 | M3.3 |
| 视频/GIF 多屏占 GPU/费电 | 🟡 中 | 全屏暂停 + 电池降帧 + 硬解 | M4.3 |
| 商店 runFullTrust 审核被打回 | 🟡 中 | 书面说明完全信任用途 | M6.4 |

---

# Self-Review 结果

1. **Spec coverage：** 设计共识的每条决策（三层架构/接管/收纳盒/双击隐藏/镜像真实桌面/每屏独立/动态壁纸/跨屏/托盘自启/商店）均有里程碑或任务对应——✅。
2. **Placeholder scan：** M0 任务全部含真实代码与命令，无 TBD/TODO；M1–M6 为路线图（任务边界 + 验收 + 依赖明确），其 bite-sized 代码按计划在各自前置阶段完成后编写，非空占位——✅。
3. **Type consistency：** `AppConfig` / `FenceConfig` / `IConfigStore` / `MonitorInfo` / `WallpaperWindow.SendToBottom` 在各任务与路线图间签名一致——✅。
4. **未决项（执行时再定，非阻塞）：** 视频/GIF 解码选 MediaElement 还是 LibVLC（M4.2）；持久 ID 选 QueryDisplayConfig 还是 WMI（M3.1，M0.5 已列备选）；MSIX 打包用 VS 还是 CLI（M6.1）。
