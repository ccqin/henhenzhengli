using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopManager.App.Controls;
using DesktopManager.App.Services;
using DesktopManager.Core.Models;
using DesktopManager.Native;

namespace DesktopManager.App.Windows;

public partial class IconLayerWindow : Window
{
    private readonly IconExtractor _icons = new();

    // ---------- T3：归属划分 + 最小接线 ----------
    // 当前所有 FenceControl 实例（T3 内存硬编码创建一个；T7 改从 ConfigStore 加载）。
    // SetIcons 全量重渲时会 Clear IconCanvas，Fence 必须在 Clear 后重 Add 才不丢（实例状态在内存）。
    private readonly List<FenceControl> _fences = new();
    // 当前已归属任一 Fence 的 FilePath 集合，SetIcons 据此过滤散落区（避免归属图标重复显示）。
    private readonly HashSet<string> _fencedPaths = new(StringComparer.OrdinalIgnoreCase);
    // 最近一次 SetIcons 的输入快照；归属变化后用它重渲散落区（保证过滤生效）。
    private IReadOnlyList<IconItem> _allItems = Array.Empty<IconItem>();
    // 散落图标项拖出候选状态（移动阈值检测，避免与双击 Open 冲突）。
    private bool _iconDragArmed;
    private string? _iconDragPath;
    private Point _iconDragOrigin;

    public IconLayerWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            // 铺主屏工作区（不含任务栏），避免遮挡任务栏。M3 多屏改按显示器工作区定位。
            var work = SystemParameters.WorkArea;
            Left = work.Left; Top = work.Top; Width = work.Width; Height = work.Height;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            WindowInterop.MakeNonInteractiveTopmost(hwnd); // 不点击穿透，可点图标
        };

        // T3 最小接线：内存硬编码一个 FenceConfig → FenceControl 加到 IconCanvas，为拖拽提供宿主。
        // T7 改为从 ConfigStore 加载 + 持久化（不在本任务）。
        CreateFence(new FenceConfig
        {
            Id = "f1",
            Title = "收纳盒",
            X = 200,
            Y = 200,
            W = 180,
            H = 120
        });
    }

    /// <summary>创建 FenceControl、Bind、加到画布、订阅归属事件。</summary>
    private void CreateFence(FenceConfig config)
    {
        var fence = new FenceControl();
        fence.Bind(config);
        Canvas.SetLeft(fence, config.X);
        Canvas.SetTop(fence, config.Y);
        fence.IconAdded += OnFenceIconAdded;
        fence.IconRemoved += OnFenceIconRemoved;
        _fences.Add(fence);
        IconCanvas.Children.Add(fence);
    }

    /// <summary>渲染散落图标列表（M1 单屏：简单网格排列，X/Y 来自 IconItem 或自动排）。
    /// T3 关键协调：散落区排除「已归属任一 Fence」的 FilePath；FenceControl 在 Clear 后重 Add（状态保留）。
    /// 被 DesktopSync.Changed 全量重渲调用时归属划分得以保持。</summary>
    public void SetIcons(IReadOnlyList<IconItem> items)
    {
        _allItems = items;
        IconCanvas.Children.Clear();
        // FenceControl 实例状态（含 ContentArea 归属图标）在内存；Clear 只断开视觉树，重 Add 后保留。
        foreach (var f in _fences) IconCanvas.Children.Add(f);

        int col = 0, row = 0;
        foreach (var item in items)
        {
            if (_fencedPaths.Contains(item.FilePath)) continue; // 已归属，不显示在散落区

            var img = new Image
            {
                Width = 32, Height = 32,
                Source = _icons.GetIcon(item.FilePath),
                Stretch = Stretch.Uniform
            };
            var label = new TextBlock
            {
                Text = item.DisplayName,
                MaxWidth = 80,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)),
                Padding = new Thickness(2, 0, 2, 0)
            };
            var panel = new StackPanel { Width = 80 };
            panel.Children.Add(img);
            panel.Children.Add(label);

            double x = item.X > 0 ? item.X : 16 + col * 90;
            double y = item.Y > 0 ? item.Y : 16 + row * 96;
            Canvas.SetLeft(panel, x);
            Canvas.SetTop(panel, y);
            panel.Tag = item.FilePath;
            panel.MouseLeftButtonDown += (_, e) =>
            {
                // 双击 Open（保留 M1 行为）；单击 arm 拖拽候选。双击不 arm，避免双击后误触发拖拽。
                if (e is MouseButtonEventArgs m && m.ClickCount >= 2)
                {
                    // review-finding 1：双击 = 两次 down，第一次(ClickCount=1)已 arm。
                    // 若不清零，双击第二次 down 后保持按住并移动 → MouseMove 满足 armed+Pressed+超阈值 → 误触 DoDragDrop。
                    _iconDragArmed = false;
                    _iconDragPath = null;
                    Open((string)panel.Tag);
                    return;
                }
                _iconDragArmed = true;
                _iconDragPath = (string)panel.Tag;
                _iconDragOrigin = e.GetPosition(this);
            };
            // review-finding 2：单击松手未移动 → 清 armed（与 FenceControl 内容图标 img.MouseLeftButtonUp 对称，
            // 避免 armed 残留到下次 down 叠加误触）。
            panel.MouseLeftButtonUp += (_, _) =>
            {
                _iconDragArmed = false;
                _iconDragPath = null;
            };
            // 拖出：左键按下且移动超阈值 → DoDragDrop（data=FilePath 字符串，DragDropEffects.Move）。
            panel.MouseMove += (_, e) =>
            {
                if (!_iconDragArmed || _iconDragPath is null) return;
                if (e.LeftButton != MouseButtonState.Pressed) return;
                var pos = e.GetPosition(this);
                if (Math.Abs(pos.X - _iconDragOrigin.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(pos.Y - _iconDragOrigin.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    var path = _iconDragPath;
                    _iconDragArmed = false;
                    _iconDragPath = null;
                    DragDrop.DoDragDrop(panel, path, DragDropEffects.Move);
                }
            };
            IconCanvas.Children.Add(panel);

            if (++col >= 10) { col = 0; row++; }
        }
    }

    // ---------- T3：Fence 归属事件回调 ----------

    private void OnFenceIconAdded(FenceControl fence, string filePath)
    {
        _fencedPaths.Add(filePath);
        // review-finding 3：跨 Fence 迁移防双重归属。拖入本 fence 后，若 path 仍属其他 Fence（如从 A 拖到 B），
        // 把它从那些 Fence **静默**移除（RemoveIconSilent 不触发 IconRemoved）。
        // 关键时序：若用普通 RemoveIcon 会触发 OnFenceIconRemoved → _fencedPaths.Remove(filePath)，
        // 但此时新 owner fence 仍拥有 filePath，_fencedPaths 必须保留它 → 会错误丢失归属。
        // 静默移除只清原 owner 的 _contentIcons/_config/UI，不影响 _fencedPaths，保证 path 最终单归属且 _fencedPaths 含 path。
        foreach (var other in _fences.Where(f => f != fence && f.ContainsIcon(filePath)))
        {
            other.RemoveIconSilent(filePath);
        }
        // 异步重渲散落区：不在 Drop 回调里同步改视觉树（避免事件源元素正被重渲的隐患，同 App.xaml.cs I-5 模式）。
        Dispatcher.BeginInvoke(new Action(() => SetIcons(_allItems)));
    }

    private void OnFenceIconRemoved(FenceControl fence, string filePath)
    {
        _fencedPaths.Remove(filePath);
        Dispatcher.BeginInvoke(new Action(() => SetIcons(_allItems)));
    }

    // ---------- T3：画布空白 Drop（从 Fence 内容区拖出到空白） ----------

    private void IconCanvas_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.Text) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void IconCanvas_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.Text)) { e.Handled = true; return; }
        var path = (string)e.Data.GetData(DataFormats.Text);
        // 定位该 FilePath 的归属 Fence；从其移除（触发 IconRemoved → 异步重渲散落区，图标自动回填）。
        // 若 path 不属于任何 Fence（散落图标拖到空白），无 owner，什么都不做。
        var owner = _fences.FirstOrDefault(f => f.ContainsIcon(path));
        owner?.RemoveIcon(path);
        e.Handled = true;
    }

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { /* M1 真机验收记录失败 case */ }
    }
}
