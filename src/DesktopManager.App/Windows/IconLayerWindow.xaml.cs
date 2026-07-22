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
            panel.MouseLeftButtonDown += (_, e) =>
            {
                if (e is MouseButtonEventArgs m && m.ClickCount >= 2)
                    Open((string)panel.Tag);
            };
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
