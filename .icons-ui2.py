# -*- coding: utf-8 -*-
# ===== 6. IconLayerWindow.xaml.cs：IconSize/LabelStyle/LabelWidth DP + 网格间距动态 =====
p = 'src/DesktopManager.Player.Icons/IconLayerWindow.xaml.cs'
s = open(p, encoding='utf-8').read()

old = '''    /// <summary>双击空白切它 → 所有散落图标项显隐由绑定自动同步；FenceControl 仍遍历 IconCanvas.Children 切 Visibility。</summary>'''
new = '''    // ---------- M6 美化：外观 DP（主进程 SetAppearance IPC 下发） ----------
    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(nameof(IconSize), typeof(int), typeof(IconLayerWindow),
            new PropertyMetadata(48, (d, _) => ((IconLayerWindow)d).OnAppearanceChanged()));
    /// <summary>图标尺寸档：32/48/64（绑定模板 Image 与 FenceControl 图标）。</summary>
    public int IconSize
    {
        get => (int)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public static readonly DependencyProperty LabelStyleProperty =
        DependencyProperty.Register(nameof(LabelStyle), typeof(string), typeof(IconLayerWindow),
            new PropertyMetadata("shadow"));
    /// <summary>文字标签风格：shadow（原生阴影，默认）/ pill（现代胶囊）。</summary>
    public string LabelStyle
    {
        get => (string)GetValue(LabelStyleProperty);
        set => SetValue(LabelStyleProperty, value);
    }

    /// <summary>标签最大宽度（跟随 IconSize，供模板绑定——XAML 无法做属性算术）。</summary>
    public double LabelWidth => IconSize + 32;

    private void OnAppearanceChanged()
    {
        OnPropertyChanged(nameof(LabelWidth));          // LabelWidth 是计算属性，手动通知
        _icons.Size = IconSize;                         // 提取尺寸档同步（缓存 key 含尺寸）
    }

    // INPC（供 LabelWidth 绑定刷新）
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>双击空白切它 → 所有散落图标项显隐由绑定自动同步；FenceControl 仍遍历 IconCanvas.Children 切 Visibility。</summary>'''
assert old in s, 'dp anchor'
s = s.replace(old, new, 1)

# 网格间距动态（FindFreeLooseSlot 按尺寸档）
old2 = '''    private (double x, double y) FindFreeLooseSlot()
    {
        const double originX = 16, originY = 16;
        const double stepX = 90, stepY = 96;
        const int cols = 10;'''
new2 = '''    private (double x, double y) FindFreeLooseSlot()
    {
        const double originX = 16, originY = 16;
        // 间距跟随图标尺寸档（M6 美化）：32→90x96 / 48→100x116 / 64→120x140
        double stepX = IconSize <= 32 ? 90 : IconSize <= 48 ? 100 : 120;
        double stepY = IconSize <= 32 ? 96 : IconSize <= 48 ? 116 : 140;
        const int cols = 10;'''
assert old2 in s, 'grid anchor'
s = s.replace(old2, new2, 1)

# using INPC
if 'using System.ComponentModel;' not in s:
    s = s.replace('using System.IO;', 'using System.ComponentModel;\nusing System.IO;', 1)
open(p, 'w', encoding='utf-8').write(s)
print('iconlayer ok')

# ===== 7. FenceControl：图标尺寸绑窗口 DP =====
p2 = 'src/DesktopManager.Player.Icons/FenceControl.xaml.cs'
s2 = open(p2, encoding='utf-8').read()
import re
# AddIcon/LoadIcons 里创建 Image 的地方（找 Width = 32 或类似）
m = re.search(r'new Image\s*\{[^}]*Width\s*=\s*32[^}]*\}', s2)
print('Image 32 found:', bool(m))
if m:
    s2 = s2.replace(m.group(0), m.group(0).replace('Width = 32', 'Width = Double.NaN'))
open(p2, 'w', encoding='utf-8').write(s2)

# ===== 8. 子进程 App：SetAppearance 处理 =====
p3 = 'src/DesktopManager.Player.Icons/App.xaml.cs'
s3 = open(p3, encoding='utf-8').read()
old3 = '''                case Show: _window.Show(); break;'''
new3 = '''                case Show: _window.Show(); break;
                case SetAppearance ap:
                    _window.LabelStyle = ap.LabelStyle;
                    _window.IconSize = ap.IconSize;
                    break;'''
assert old3 in s3
s3 = s3.replace(old3, new3, 1)
open(p3, 'w', encoding='utf-8').write(s3)
print('icons app ok')
