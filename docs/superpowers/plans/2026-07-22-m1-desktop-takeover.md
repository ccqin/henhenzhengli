# M1 桌面接管 + 图标镜像核心 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 app 启动后接管 explorer 桌面图标显示（隐藏原生图标）、自绘一个图标层窗口渲染真实桌面文件夹的图标、桌面文件增删自动同步、并在崩溃/explorer 重启时正确恢复——把"桌面图标管理"从空壳变成真正可用的接管层。

**Architecture:** Core 负责纯逻辑（IconItem 模型、读桌面文件夹快照、对账 diff）；Native 负责 Win32（SHGetFileInfo 提取 HICON、explorer 重启监听）；App 负责 WPF（IconExtractor 转 BitmapSource+缓存、IconLayerWindow 渲染、RecoveryGuard 接管状态机、接线）。接管 explorer 用 M0 的 `DesktopIconVisibility`；窗口基底用 M0 的 `WindowInterop.MakeNonInteractiveTopmost`（不点击穿透，要能点图标）。

**Tech Stack:** C# / WPF / .NET 10 / xUnit / Win32 P/Invoke（SHGetFileInfo、TaskbarCreated 广播）/ M0 已就绪的 DesktopIconVisibility + WindowInterop + ConfigStore。

## Global Constraints

- 沿用 M0：Core `net10.0`；Native `net10.0-windows`；App `net10.0-windows10.0.19041.0`；Tests `net10.0`。
- Core 不得依赖 WPF/UI 类型（图标图像 BitmapSource 只在 App 层）。
- 桌面文件只读用户自己的 `Desktop` + 公共 `CommonDesktop`，不写系统路径。
- 接管状态可观测：app 正常退出必须恢复 explorer 显示；崩溃/被杀/explorer 重启后，下次启动或重启事件触发时恢复一致性。
- 图标层窗口不点击穿透（区别于 M0 的 WallpaperWindow），要能接收点击/双击。
- M1 只做**单屏**（多屏是 M3）；图标坐标先用简单 `(X,Y)`。
- 精确 `git add <paths>`，不 `git add -A`（`.superpowers/` 已 ignored）。
- TDD：Core 纯逻辑（快照读取、对账 diff、RecoveryGuard 状态判断）用 xUnit；Win32/UI 用 spike + 真机验收。

## M1 任务总览

| 执行单元 | 任务 | 性质 | 验收 |
|---|---|---|---|
| M1-T1 | IconItem 模型 + DesktopSnapshot 读桌面文件夹 | TDD | 单测：读测试 fixture 文件夹返回正确 IconItem 列表 |
| M1-T2 | DesktopSync 对账 diff 算法 | TDD | 单测：增/删/改名三类 diff 正确 |
| M1-T3 | FileSystemWatcher 集成（事件 + 对账触发） | 代码+集成 | FSW 事件 + 定时对账双保险触发 diff |
| M1-T4 | IconExtractor（SHGetFileInfo→BitmapSource+缓存） | spike | 给 .lnk/.exe/.txt 提取正确图标，缓存命中 |
| M1-T5 | IconLayerWindow 渲染图标 + 双击打开 | UI | 图标按坐标显示，双击 ShellExecute 打开 |
| M1-T6 | RecoveryGuard（接管状态机 + 崩溃恢复） | TDD+接线 | 启动检测上次接管、正常退出恢复、崩溃后下次启动恢复 |
| M1-T7 | ShellRestartWatcher（explorer 重启监听） | spike | 任务管理器重启 explorer 后重新接管 |
| M1-T8 | 接线（App 启动接入全流程）+ 冒烟 | 集成 | 启动→接管→图标层显示真实图标→改文件同步→退出恢复 |

## 文件结构（M1 新增/改动）

```
src/DesktopManager.Core/
├── Models/
│   ├── AppConfig.cs            # 已有（M0）
│   └── IconItem.cs             # 新：record IconItem(FilePath, DisplayName, X, Y)
├── Services/
│   ├── ConfigStore.cs          # 已有
│   ├── IDesktopSnapshot.cs     # 新
│   ├── DesktopSnapshot.cs      # 新：读 Desktop+CommonDesktop → IconItem 列表
│   ├── DesktopDiff.cs          # 新：diff 结果 record + Diff() 静态算法
│   └── DesktopSync.cs          # 新：持快照 + FSW + 定时对账，发出变更事件
src/DesktopManager.Native/
├── DesktopIconVisibility.cs    # 已有（M0）
├── MonitorEnumerator.cs        # 已有
├── WindowInterop.cs            # 已有（M0 final fix）
├── IconExtractorNative.cs      # 新：SHGetFileInfo P/Invoke，返回 HICON(IntPtr)
└── ShellRestartWatcher.cs      # 新：TaskbarCreated 广播监听（或放 App，见 T7）
src/DesktopManager.App/
├── App.xaml(.cs)               # 改：接线 RecoveryGuard + IconLayerWindow + Sync
├── Windows/
│   ├── WallpaperWindow.xaml(.cs) # 已有
│   └── IconLayerWindow.xaml(.cs) # 新：渲染图标层（不点击穿透）
├── Services/
│   └── IconExtractor.cs        # 新：HICON→BitmapSource + 缓存
└── RecoveryGuard.cs            # 新：接管状态机
src/DesktopManager.Tests/
├── ConfigStoreTests.cs         # 已有
├── DesktopSnapshotTests.cs     # 新
├── DesktopDiffTests.cs         # 新
└── RecoveryGuardTests.cs       # 新（状态判断部分）
```

## 测试策略

- **TDD（xUnit）**：DesktopSnapshot（用临时测试文件夹 fixture）、DesktopDiff（纯算法）、RecoveryGuard 状态判断（注入接口 mock 注册表状态）。
- **spike + 真机验收**：IconExtractor（SHGetFileInfo 行为）、IconLayerWindow（渲染+双击）、ShellRestartWatcher（explorer 重启）。无法可靠自动化。

---

# 详细任务

## M1-T1 — IconItem 模型 + DesktopSnapshot（TDD）

**Files:**
- Create: `src/DesktopManager.Core/Models/IconItem.cs`
- Create: `src/DesktopManager.Core/Services/IDesktopSnapshot.cs`
- Create: `src/DesktopManager.Core/Services/DesktopSnapshot.cs`
- Test: `src/DesktopManager.Tests/DesktopSnapshotTests.cs`

**Interfaces:**
- Produces: `IconItem(string FilePath, string DisplayName, double X, double Y)`；`IDesktopSnapshot.Capture() → IReadOnlyList<IconItem>`。

- [ ] **Step 1: 写失败测试**

`src/DesktopManager.Tests/DesktopSnapshotTests.cs`:
```csharp
using DesktopManager.Core.Models;
using DesktopManager.Core.Services;

namespace DesktopManager.Tests;

public class DesktopSnapshotTests
{
    [Fact]
    public void Capture_ReturnsFilesFromFolder()
    {
        var dir = Directory.CreateTempSubdirectory("dm_snap_");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "a.txt"), "");
            File.WriteAllText(Path.Combine(dir.FullName, "b.lnk"), "");
            var snap = new DesktopSnapshot(dir.FullName);
            var items = snap.Capture();
            Assert.Equal(2, items.Count);
            Assert.Contains(items, i => i.DisplayName == "a.txt");
            Assert.Contains(items, i => i.FilePath.EndsWith("b.lnk"));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Capture_EmptyFolder_ReturnsEmpty()
    {
        var dir = Directory.CreateTempSubdirectory("dm_empty_");
        try
        {
            var snap = new DesktopSnapshot(dir.FullName);
            Assert.Empty(snap.Capture());
        }
        finally { dir.Delete(recursive: true); }
    }
}
```

- [ ] **Step 2: 跑确认失败**
`dotnet test src/DesktopManager.Tests/DesktopManager.Tests.csproj --filter "FullyQualifiedName~DesktopSnapshotTests"` → FAIL（类型未定义）。

- [ ] **Step 3: 实现**

`src/DesktopManager.Core/Models/IconItem.cs`:
```csharp
namespace DesktopManager.Core.Models;

public record IconItem(string FilePath, string DisplayName, double X = 0, double Y = 0);
```

`src/DesktopManager.Core/Services/IDesktopSnapshot.cs`:
```csharp
using DesktopManager.Core.Models;
namespace DesktopManager.Core.Services;

public interface IDesktopSnapshot
{
    IReadOnlyList<IconItem> Capture();
}
```

`src/DesktopManager.Core/Services/DesktopSnapshot.cs`:
```csharp
using System.IO;
using DesktopManager.Core.Models;
namespace DesktopManager.Core.Services;

/// <summary>读取桌面文件夹（用户 Desktop + 公共 CommonDesktop）为 IconItem 快照。
/// 构造接受文件夹路径列表（便于测试注入 fixture）。</summary>
public sealed class DesktopSnapshot : IDesktopSnapshot
{
    private readonly string[] _folders;

    public DesktopSnapshot(params string[] folders) => _folders = folders;

    /// <summary>默认：用户桌面 + 公共桌面。</summary>
    public static DesktopSnapshot ForDefaultDesktops() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));

    public IReadOnlyList<IconItem> Capture()
    {
        var items = new List<IconItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in _folders)
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                if (!seen.Add(path)) continue; // 用户/公共桌面同名去重
                items.Add(new IconItem(path, Path.GetFileName(path)));
            }
        }
        return items;
    }
}
```

- [ ] **Step 4: 跑确认通过** → 2/2 PASS。
- [ ] **Step 5: 提交**
```bash
git add src/DesktopManager.Core/Models/IconItem.cs src/DesktopManager.Core/Services/IDesktopSnapshot.cs src/DesktopManager.Core/Services/DesktopSnapshot.cs src/DesktopManager.Tests/DesktopSnapshotTests.cs
git commit -m "feat(core): IconItem model and DesktopSnapshot with folder fixture tests"
```

---

## M1-T2 — DesktopDiff 对账算法（TDD）

**Files:**
- Create: `src/DesktopManager.Core/Services/DesktopDiff.cs`
- Test: `src/DesktopManager.Tests/DesktopDiffTests.cs`

**Interfaces:**
- Produces: `DesktopDiff(IReadOnlyList<IconItem> Added, Removed, IReadOnlyList<(IconItem Old, IconItem New)> Renamed)`；`DesktopDiff.Diff(prev, cur)`（按 FilePath 比对，FilePath 变但同目录同名视为改名——简化：M1 先按 FilePath 精确匹配，改名 = 删旧+加新，不做 rename 推断；rename 推断留 M2）。

> **设计简化**：M1 的 diff 只做 Added（cur 有 prev 无）/ Removed（prev 有 cur 无），不做 rename 推断（需要启发式，留 M2）。测试只覆盖 Added/Removed。

- [ ] **Step 1: 写失败测试**

`src/DesktopManager.Tests/DesktopDiffTests.cs`:
```csharp
using DesktopManager.Core.Models;
using DesktopManager.Core.Services;

namespace DesktopManager.Tests;

public class DesktopDiffTests
{
    private static IconItem I(string name) => new("C:\\" + name, name);

    [Fact]
    public void Diff_NoChange_ReturnsEmpty()
    {
        var prev = new[] { I("a.txt"), I("b.txt") };
        var diff = DesktopDiff.Diff(prev, prev);
        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
    }

    [Fact]
    public void Diff_AddedAndRemoved()
    {
        var prev = new[] { I("a.txt"), I("b.txt") };
        var cur  = new[] { I("b.txt"), I("c.txt") }; // 删 a，加 c
        var diff = DesktopDiff.Diff(prev, cur);
        Assert.Equal(new[] { "c.txt" }, diff.Added.Select(i => i.DisplayName));
        Assert.Equal(new[] { "a.txt" }, diff.Removed.Select(i => i.DisplayName));
    }
}
```

- [ ] **Step 2: 跑确认失败**。
- [ ] **Step 3: 实现**

`src/DesktopManager.Core/Services/DesktopDiff.cs`:
```csharp
using DesktopManager.Core.Models;
namespace DesktopManager.Core.Services;

public record DesktopDiff(
    IReadOnlyList<IconItem> Added,
    IReadOnlyList<IconItem> Removed);

public static class DesktopDiffCalculator
{
    public static DesktopDiff Diff(IReadOnlyList<IconItem> previous, IReadOnlyList<IconItem> current)
    {
        var prevByPath = previous.ToDictionary(i => i.FilePath, StringComparer.OrdinalIgnoreCase);
        var curByPath  = current.ToDictionary(i => i.FilePath, StringComparer.OrdinalIgnoreCase);

        var added = current.Where(c => !prevByPath.ContainsKey(c.FilePath)).ToList();
        var removed = previous.Where(p => !curByPath.ContainsKey(p.FilePath)).ToList();
        return new DesktopDiff(added, removed);
    }
}
```
> 注：静态类名 `DesktopDiffCalculator`，diff 方法在此；`DesktopDiff` 是 record 结果。若 reviewer 偏好统一到 `DesktopDiff.Diff`，可把 Diff 挂到 record 上——执行时定，测试用 `DesktopDiff.Diff` 调用，实现须匹配（把 Diff 做成 `DesktopDiff` record 的静态方法，或测试改调 `DesktopDiffCalculator.Diff`）。**统一约定**：测试调 `DesktopDiff.Diff(...)`，故把 Diff 实现为 `DesktopDiff` 上的 `public static` 方法：
```csharp
public record DesktopDiff(...)
{
    public static DesktopDiff Diff(IReadOnlyList<IconItem> previous, IReadOnlyList<IconItem> current)
    {
        var prevByPath = previous.ToDictionary(i => i.FilePath, StringComparer.OrdinalIgnoreCase);
        var curByPath  = current.ToDictionary(i => i.FilePath, StringComparer.OrdinalIgnoreCase);
        var added = current.Where(c => !prevByPath.ContainsKey(c.FilePath)).ToList();
        var removed = previous.Where(p => !curByPath.ContainsKey(p.FilePath)).ToList();
        return new DesktopDiff(added, removed);
    }
}
```

- [ ] **Step 4: 跑确认通过** → 2/2 PASS。
- [ ] **Step 5: 提交**
```bash
git add src/DesktopManager.Core/Services/DesktopDiff.cs src/DesktopManager.Tests/DesktopDiffTests.cs
git commit -m "feat(core): DesktopDiff added/removed change-set algorithm"
```

---

## M1-T3 — FileSystemWatcher + 定时对账（DesktopSync）

**Files:**
- Create: `src/DesktopManager.Core/Services/DesktopSync.cs`

**Interfaces:**
- Consumes: `IDesktopSnapshot.Capture()`、`DesktopDiff.Diff()`。
- Produces: `DesktopSync`（持当前快照，订阅 FSW 事件 + 定时对账，`event EventHandler<DesktopDiff>? Changed`）。

- [ ] **Step 1: 实现（非纯逻辑，含 FSW/定时器，不强制单测；靠 T5 真机验收）**

`src/DesktopManager.Core/Services/DesktopSync.cs`:
```csharp
using System.IO;
namespace DesktopManager.Core.Services;

/// <summary>监听桌面文件变化：FileSystemWatcher 事件 + 定时全量对账双保险（FSW 漏事件时对账兜底）。</summary>
public sealed class DesktopSync : IDisposable
{
    private readonly IDesktopSnapshot _snapshot;
    private readonly FileSystemWatcher[] _watchers;
    private readonly Timer _reconcileTimer;
    private IReadOnlyList<IconItem> _current;
    private readonly object _lock = new();

    public event EventHandler<DesktopDiff>? Changed;

    public DesktopSync(IDesktopSnapshot snapshot, IEnumerable<string> folders, TimeSpan reconcileInterval)
    {
        _snapshot = snapshot;
        _current = snapshot.Capture();
        _watchers = folders.Where(Directory.Exists).Select(f =>
        {
            var w = new FileSystemWatcher(f) { IncludeSubdirectories = false, EnableRaisingEvents = true };
            w.Created += (_, _) => Reconcile();
            w.Deleted += (_, _) => Reconcile();
            w.Renamed += (_, _) => Reconcile();
            return w;
        }).ToArray();
        _reconcileTimer = new Timer(_ => Reconcile(), null, reconcileInterval, reconcileInterval);
    }

    public IReadOnlyList<IconItem> Current { get { lock (_lock) return _current; } }

    private void Reconcile()
    {
        DesktopDiff? diff = null;
        lock (_lock)
        {
            var latest = _snapshot.Capture();
            diff = DesktopDiff.Diff(_current, latest);
            if (diff.Added.Count == 0 && diff.Removed.Count == 0) return;
            _current = latest;
        }
        Changed?.Invoke(this, diff);
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _reconcileTimer.Dispose();
    }
}
```
> FSW 事件和定时器都触发同一个 `Reconcile()`（全量对账），天然幂等，规避 FSW 漏事件/重复事件。

- [ ] **Step 2: build 通过**（`dotnet build DesktopManager.sln`）。
- [ ] **Step 3: 提交**
```bash
git add src/DesktopManager.Core/Services/DesktopSync.cs
git commit -m "feat(core): DesktopSync with FSW + periodic reconcile dual safety"
```

---

## M1-T4 — IconExtractor（SHGetFileInfo → BitmapSource + 缓存）spike

**Files:**
- Create: `src/DesktopManager.Native/IconExtractorNative.cs`（SHGetFileInfo P/Invoke，返回 HICON IntPtr）
- Create: `src/DesktopManager.App/Services/IconExtractor.cs`（HICON → BitmapSource + 字典缓存）

**Interfaces:**
- Produces: `IconExtractorNative.GetHIcon(string filePath, int size) → IntPtr`（0 表示失败）；`IconExtractor.GetIcon(string filePath) → BitmapSource?`（带缓存）。

- [ ] **Step 1: Native SHGetFileInfo 封装**

`src/DesktopManager.Native/IconExtractorNative.cs`:
```csharp
using System.Runtime.InteropServices;
namespace DesktopManager.Native;

public static class IconExtractorNative
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]  public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>提取文件图标 HICON。size=32 走大图标。返回 IntPtr.Zero 表示失败。</summary>
    public static IntPtr GetHIcon(string filePath, int size = 32)
    {
        var fi = new SHFILEINFO();
        uint flags = SHGFI_ICON | (size <= 16 ? SHGFI_SMALLICON : SHGFI_LARGEICON);
        SHGetFileInfo(filePath, 0, ref fi, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        return fi.hIcon;
    }

    /// <summary>调用方取完 BitmapSource 后应释放 HICON。</summary>
    public static void Destroy(IntPtr hIcon)
    {
        if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
    }
}
```

- [ ] **Step 2: App 层转 BitmapSource + 缓存**

`src/DesktopManager.App/Services/IconExtractor.cs`:
```csharp
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using DesktopManager.Native;

namespace DesktopManager.App.Services;

public sealed class IconExtractor
{
    private readonly Dictionary<string, BitmapSource> _cache = new(StringComparer.OrdinalIgnoreCase);

    public BitmapSource? GetIcon(string filePath)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(filePath, out var hit)) return hit;
        }
        IntPtr hicon = IconExtractorNative.GetHIcon(filePath);
        if (hicon == IntPtr.Zero) return null;
        try
        {
            var bmp = Imaging.CreateBitmapSourceFromHIcon(hicon, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bmp.Freeze(); // 跨线程可用
            lock (_cache) { _cache[filePath] = bmp; }
            return bmp;
        }
        finally { IconExtractorNative.Destroy(hicon); }
    }
}
```

- [ ] **Step 3: build 通过**。
- [ ] **Step 4: spike 验收（真机，T8 接线后或临时挂调用）**：给 `.txt`/`.exe`/`.lnk` 提取图标，确认返回正确缩略图（非 null、视觉正确）；同一路径第二次取命中缓存。记录在 `docs/superpowers/notes/m1-spikes.md`。
- [ ] **Step 5: 提交**
```bash
git add src/DesktopManager.Native/IconExtractorNative.cs src/DesktopManager.App/Services/IconExtractor.cs
git commit -m "feat(native,app): IconExtractor SHGetFileInfo->BitmapSource with cache"
```

---

## M1-T5 — IconLayerWindow 渲染图标 + 双击打开

**Files:**
- Create: `src/DesktopManager.App/Windows/IconLayerWindow.xaml`
- Create: `src/DesktopManager.App/Windows/IconLayerWindow.xaml.cs`

**Interfaces:**
- Consumes: `WindowInterop.MakeNonInteractiveTopmost`（M0）、`IconExtractor.GetIcon`、`IconItem`。
- Produces: `IconLayerWindow`，可 `SetIcons(IReadOnlyList<IconItem>)`、全屏置底不点击穿透、双击图标 ShellExecute 打开。

- [ ] **Step 1: XAML（Canvas 承载图标项）**

`src/DesktopManager.App/Windows/IconLayerWindow.xaml`:
```xml
<Window x:Class="DesktopManager.App.Windows.IconLayerWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="IconLayer" WindowStyle="None" ResizeMode="NoResize"
        ShowInTaskbar="False" Background="Transparent">
    <Canvas x:Name="IconCanvas"/>
</Window>
```

- [ ] **Step 2: code-behind**

`src/DesktopManager.App/Windows/IconLayerWindow.xaml.cs`:
```csharp
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopManager.App.Services;
using DesktopManager.Core.Models;
using DesktopManager.Native;

namespace DesktopManager.App.Windows;

public partial class IconLayerWindow : Window
{
    private readonly IconExtractor _icons = new();

    public IconLayerWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            WindowInterop.MakeNonInteractiveTopmost(hwnd); // 不点击穿透，可点图标
        };
    }

    /// <summary>渲染图标列表（M1 单屏：简单网格排列，X/Y 来自 IconItem 或自动排）。</summary>
    public void SetIcons(IReadOnlyList<IconItem> items)
    {
        IconCanvas.Children.Clear();
        int col = 0, row = 0;
        foreach (var item in items)
        {
            var img = new Image
            {
                Width = 32, Height = 32,
                Source = _icons.GetIcon(item.FilePath),
                Stretch = Stretch.Uniform
            };
            var label = new TextBlock { Text = item.DisplayName, MaxWidth = 80, TextWrapping = TextWrapping.Wrap };
            var panel = new StackPanel { Width = 80 };
            panel.Children.Add(img);
            panel.Children.Add(label);

            double x = item.X > 0 ? item.X : 16 + col * 90;
            double y = item.Y > 0 ? item.Y : 16 + row * 96;
            Canvas.SetLeft(panel, x);
            Canvas.SetTop(panel, y);
            panel.Tag = item.FilePath;
            panel.MouseLeftButtonDown += (_, _) => Open((string)panel.Tag);
            IconCanvas.Children.Add(panel);

            if (++col >= 10) { col = 0; row++; }
        }
    }

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { /* M1 真机验收记录失败 case */ }
    }
}
```

- [ ] **Step 3: build 通过**。
- [ ] **Step 4: spike 验收（真机，T8 接线后）**：图标层显示真实桌面图标（图片+名称），双击用关联程序打开；图标层在 WallpaperWindow 之上、普通窗口之下、可点击（不穿透）。
- [ ] **Step 5: 提交**
```bash
git add src/DesktopManager.App/Windows/IconLayerWindow.xaml src/DesktopManager.App/Windows/IconLayerWindow.xaml.cs
git commit -m "feat(app): IconLayerWindow renders desktop icons with double-click open"
```

---

## M1-T6 — RecoveryGuard（接管状态机 + 崩溃恢复）

**Files:**
- Create: `src/DesktopManager.App/RecoveryGuard.cs`
- Test: `src/DesktopManager.Tests/RecoveryGuardTests.cs`（状态判断部分）

**设计**：
- 接管状态信号 = explorer 注册表 `HideIcons==1`（M0 的 DesktopIconVisibility 写的就是这个键，explorer 重启保持）。
- 启动时 `DetectState()`：若 `HideIcons==1` → 上次接管过（可能崩溃，因为正常退出会恢复成 0）→ 返回 `PreviouslyTakenOver`。
- `TakeOver()`：`DesktopIconVisibility.HideDesktopIcons()` + 持久化 AppConfig。
- `RestoreExplorer()`：`DesktopIconVisibility.ShowDesktopIcons()`。
- 正常退出调 `RestoreExplorer`；崩溃来不及调 → 下次启动 `DetectState()==PreviouslyTakenOver` → app 知道是崩溃恢复，先 Restore 再 TakeOver（或直接 TakeOver 保持接管）。

**Interfaces:**
- Produces: `RecoveryGuard`（注入 `Func<bool>` 读 HideIcons、`Action<bool>` 设 HideIcons，便于单测）。

- [ ] **Step 1: 写失败测试（状态判断）**

`src/DesktopManager.Tests/RecoveryGuardTests.cs`:
```csharp
using DesktopManager.App;
namespace DesktopManager.Tests;

public class RecoveryGuardTests
{
    [Theory]
    [InlineData(true, RecoveryState.PreviouslyTakenOver)]
    [InlineData(false, RecoveryState.Clean)]
    public void DetectState_ReflectsHideIcons(bool hideIcons, RecoveryState expected)
    {
        var guard = new RecoveryGuard(() => hideIcons, _ => { });
        Assert.Equal(expected, guard.DetectState());
    }

    [Fact]
    public void TakeOver_SetsHideIconsTrue()
    {
        bool set = false;
        var guard = new RecoveryGuard(() => false, v => set = v);
        guard.TakeOver();
        Assert.True(set);
    }

    [Fact]
    public void RestoreExplorer_SetsHideIconsFalse()
    {
        bool set = true;
        var guard = new RecoveryGuard(() => true, v => set = v);
        guard.RestoreExplorer();
        Assert.False(set);
    }
}
```
> Tests 项目需引用 App 项目（之前只引用 Core）。`dotnet add src/DesktopManager.Tests/DesktopManager.Tests.csproj reference src/DesktopManager.App/DesktopManager.App.csproj`——但 App 是 WinExe/WPF，Tests(net10.0) 引用 App(net10.0-windows) 可能 TFM 不兼容。**替代**：把 RecoveryGuard 的纯状态逻辑抽到 Core（`RecoveryState` enum + 接受 `Func<bool>`/`Action<bool>` 的判断器），App 层薄封装调 DesktopIconVisibility。这样 Tests 引用 Core 即可测。

**修正设计**：Core 放 `RecoveryState` + `RecoveryStateDetector`（纯逻辑）；App 放 `RecoveryGuard`（调 Core detector + DesktopIconVisibility）。

`src/DesktopManager.Core/Services/RecoveryStateDetector.cs`（Core）:
```csharp
namespace DesktopManager.Core.Services;

public enum RecoveryState { Clean, PreviouslyTakenOver }

public sealed class RecoveryStateDetector
{
    private readonly Func<bool> _isHidden;
    public RecoveryStateDetector(Func<bool> isHidden) => _isHidden = isHidden;
    public RecoveryState Detect() => _isHidden() ? RecoveryState.PreviouslyTakenOver : RecoveryState.Clean;
}
```

测试改为测 Core 的 `RecoveryStateDetector`（Tests 已引用 Core），不引用 App。RecoveryGuard(App) 只是调 detector + DesktopIconVisibility 的薄壳，靠真机验收。

- [ ] **Step 2: 跑确认失败** → 类型未定义。
- [ ] **Step 3: 实现 Core detector**（如上）。

`src/DesktopManager.App/RecoveryGuard.cs`（薄壳）:
```csharp
using DesktopManager.Core.Services;
using DesktopManager.Native;

namespace DesktopManager.App;

public sealed class RecoveryGuard
{
    private readonly RecoveryStateDetector _detector =
        new(() => DesktopIconVisibility.IsHidden()); // 见下，需给 DesktopIconVisibility 加 IsHidden()

    public RecoveryState DetectState() => _detector.Detect();

    public void TakeOver() => DesktopIconVisibility.HideDesktopIcons();

    public void RestoreExplorer() => DesktopIconVisibility.ShowDesktopIcons();
}
```
> 需给 M0 的 `DesktopIconVisibility` 加 `public static bool IsHidden()`（读注册表 HideIcons 当前值）。

- [ ] **Step 4: 测试改测 RecoveryStateDetector（Core），跑通过** → 3/3。
- [ ] **Step 5: 提交**
```bash
git add src/DesktopManager.Core/Services/RecoveryStateDetector.cs src/DesktopManager.Native/DesktopIconVisibility.cs src/DesktopManager.App/RecoveryGuard.cs src/DesktopManager.Tests/RecoveryGuardTests.cs
git commit -m "feat(core,app): RecoveryGuard takeover state machine with crash-recovery detection"
```

---

## M1-T7 — ShellRestartWatcher（explorer 重启监听）spike

**Files:**
- Create: `src/DesktopManager.Native/ShellRestartWatcher.cs`（或 App，见设计）

**机制**：explorer 重启后会广播 `RegisterWindowMessage("TaskbarCreated")`。WPF 里通过窗口的 WndProc（HwndSource.AddHook）监听该消息。

**设计**：放 App 层（要 HwndSource hook）。`ShellRestartWatcher` 接受一个 hwnd，AddHook 监听 TaskbarCreated，触发 `event Action? ExplorerRestarted`。

- [ ] **Step 1: 实现**

`src/DesktopManager.App/ShellRestartWatcher.cs`:
```csharp
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace DesktopManager.App;

/// <summary>监听 explorer.exe 重启（TaskbarCreated 广播），触发重新接管。</summary>
public sealed class ShellRestartWatcher
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    private readonly uint _taskbarCreated;

    public event Action? ExplorerRestarted;

    public ShellRestartWatcher() => _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

    public void Attach(IntPtr hwnd)
    {
        var src = HwndSource.FromHwnd(hwnd);
        if (src == null) return;
        src.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg == _taskbarCreated) ExplorerRestarted?.Invoke();
        return IntPtr.Zero;
    }
}
```

- [ ] **Step 2: build 通过**。
- [ ] **Step 3: spike 验收（真机）**：app 接管中 → 任务管理器重启 explorer.exe → `ExplorerRestarted` 触发 → 重新 HideDesktopIcons（explorer 重启会把 HideIcons 恢复成 explorer 默认，需重新应用）。记录在 m1-spikes.md。
- [ ] **Step 4: 提交**
```bash
git add src/DesktopManager.App/ShellRestartWatcher.cs
git commit -m "feat(app): ShellRestartWatcher via TaskbarCreated broadcast"
```

---

## M1-T8 — 接线 + 冒烟

**Files:**
- Modify: `src/DesktopManager.App/App.xaml.cs`（接入 RecoveryGuard + IconLayerWindow + DesktopSync + ShellRestartWatcher）

- [ ] **Step 1: App.xaml.cs 接线**

在 `OnStartup` 里（base.OnStartup 之后、托盘 ForceCreate 之后）：
```csharp
// 1. 恢复 + 接管
_recoveryGuard = new RecoveryGuard();
_recoveryGuard.TakeOver(); // 隐藏 explorer 原生图标

// 2. 图标层窗口
_iconLayer = new IconLayerWindow();
_iconLayer.Show();
var iconHwnd = new System.Windows.Interop.WindowInteropHelper(_iconLayer).Handle;

// 3. 桌面同步
var snapshot = DesktopSnapshot.ForDefaultDesktops();
_iconLayer.SetIcons(snapshot.Capture());
_sync = new DesktopSync(snapshot,
    new[] { Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) },
    TimeSpan.FromSeconds(3));
_sync.Changed += (_, diff) => Dispatcher.Invoke(() => _iconLayer.SetIcons(_sync.Current));

// 4. explorer 重启重新接管
_shellWatcher = new ShellRestartWatcher();
_shellWatcher.ExplorerRestarted += () => Dispatcher.Invoke(() => _recoveryGuard.TakeOver());
_shellWatcher.Attach(iconHwnd);
```

在 `OnExit`（M0 已加）里 Dispose + 恢复：
```csharp
protected override void OnExit(ExitEventArgs e)
{
    _sync?.Dispose();
    _recoveryGuard?.RestoreExplorer(); // 正常退出恢复 explorer 图标
    _tray?.Dispose();
    base.OnExit(e);
}
```

- [ ] **Step 2: build + 全量 test**
```bash
dotnet build DesktopManager.sln
dotnet test DesktopManager.sln
```
Expected: build 0 error；test 通过（ConfigStore 4 + Snapshot 2 + Diff 2 + RecoveryState 3 = 11）。

- [ ] **Step 3: 真机冒烟（用户验收）**
`dotnet run --project src/DesktopManager.App`：
1. 启动后 explorer 原生桌面图标消失，你的图标层窗口显示真实桌面图标（图片+名称）。
2. 双击图标 → 关联程序打开。
3. 往桌面新建/删除文件 → ≤3s 图标层同步。
4. 任务管理器重启 explorer.exe → 图标层仍接管（原生图标不回来）。
5. 托盘→退出 → explorer 原生桌面图标恢复。

- [ ] **Step 4: 写 m1-spikes.md 记录 spike 结论 + 提交 + tag**
```bash
git add src/DesktopManager.App/App.xaml.cs docs/superpowers/notes/m1-spikes.md
git commit -m "feat(app): wire desktop takeover full pipeline; M1 smoke-ready"
git tag m1-desktop-takeover
```

---

## 风险与对策（M1）

| 风险 | 对策 | 落地 |
|---|---|---|
| 崩溃后 explorer 图标不恢复（致命#1） | RecoveryGuard：注册表 HideIcons 持久 + 启动 DetectState + OnExit Restore | T6/T8 |
| explorer 重启接管失效（高#3） | ShellRestartWatcher TaskbarCreated 重新接管 | T7/T8 |
| FSW 漏事件（高#4） | 事件 + 定时全量对账双保险 | T3 |
| 图标层 z-order 与 WallpaperWindow 冲突 | IconLayer 用 MakeNonInteractiveTopmost（置底但可点）；M1 暂不叠 WallpaperWindow | T5 |
| IconExtractor 对特殊文件失败 | 返回 null + 图标层容错显示占位 | T4/T5 |
| SHGetFileInfo 性能（大量图标） | IconExtractor 缓存 | T4 |

## Self-Review

1. **Spec 覆盖**：grilling 定的"接管 explorer + 镜像真实桌面 + 崩溃恢复 + explorer 重启 + FSW 对账"全部有任务（T1-T8）✅。
2. **Placeholder**：TDD 任务（T1/T2/T6-detector）含完整代码；spike 任务（T4/T5/T7）含骨架 + 真机验收点；T8 接线含代码。无 TBD ✅。
3. **类型一致**：`IconItem(FilePath, DisplayName, X, Y)`、`IDesktopSnapshot.Capture()`、`DesktopDiff.Diff()`、`RecoveryStateDetector.Detect()` 跨任务签名一致 ✅。
4. **M0 衔接**：复用 `DesktopIconVisibility`（需加 `IsHidden()`）、`WindowInterop.MakeNonInteractiveTopmost`、`AppConfig`/`ConfigStore`、托盘 `OnExit`。已在 T6/T8 标注对 M0 的扩展点 ✅。
5. **未决（执行时定）**：rename 推断（简化为删+加，留 M2）；Tests 引用 App 的 TFM 问题（用 Core detector 规避，已标注）；IconLayer 排列算法（M1 简单网格，M2 收纳盒重排）。
