using System.Windows;
using System.Windows.Interop;
using DesktopManager.Native;

namespace DesktopManager.App.Windows;

public partial class WallpaperWindow : Window
{
    public WallpaperWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            WindowInterop.MakeClickThrough(hwnd);
        };
    }
}
