using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

class P {
  [STAThread]
  static void Main() {
    var prog = FindWindow("Progman", null);
    Console.WriteLine($"Progman=0x{prog.ToString("X")}");

    var win = new Window {
      Left = 0, Top = 0, Width = 800, Height = 600,
      WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false,
      AllowsTransparency = false, Background = Brushes.DarkSlateGray,
    };
    var panel = new StackPanel { Margin = new Thickness(20) };
    panel.Children.Add(new Label { Content = "桌面层非透明窗口测试", Foreground = Brushes.White, FontSize = 28 });
    panel.Children.Add(new Label { Content = "如果你能看到这个深灰色窗口", Foreground = Brushes.LightGreen, FontSize = 18 });
    panel.Children.Add(new Label { Content = ">>> 按 Win+D 测试抗性，此窗口应保持 <<<", Foreground = Brushes.Yellow, FontSize = 18 });
    win.Content = panel;

    win.SourceInitialized += (_, _) => {
      var hwnd = new WindowInteropHelper(win).Handle;
      Console.WriteLine($"窗口 hwnd=0x{hwnd.ToString("X")}");
      SetParent(hwnd, prog);
      var style = GetWindowLong(hwnd, -16);
      SetWindowLong(hwnd, -16, (int)((style & ~0x80000000) | 0x40000000));
      var ex = GetWindowLong(hwnd, -20);
      SetWindowLong(hwnd, -20, (int)(ex & ~0x80000));
      SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 800, 600, 0x0010);
      Console.WriteLine(">>> 窗口已显示，请按 Win+D 测试（15秒后关闭）<<<");
      var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
      t.Tick += (_, _) => win.Close();
      t.Start();
    };
    new Application().Run(win);
  }

  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string c, string t);
  [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr c, IntPtr p);
  [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int n);
  [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int n, int v);
  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
}
