using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DesktopManager.Player.Icons;

namespace DesktopManager.Player.Icons;

/// <summary>M2-T5：自定义重命名输入对话框（TextBox + OK/Cancel，半透明风格）。
/// WPF 无内置 InputBox；选自定义对话框（brief 推荐方案 a）以与 IconLayer/FenceControl 外观协调，
/// 而非 Microsoft.VisualBasic.Interaction.InputBox 的老旧外观。</summary>
public partial class RenameDialog : Window
{
    private RenameDialog()
    {
        InitializeComponent();
    }

    /// <summary>弹出重命名对话框。预填当前完整文件名（选中的是主名、不含扩展名，贴 Explorer 习惯）。
    /// 返回用户输入的新文件名（已 Trim）；用户取消返回 null。由调用方做 ResolveRenamePath 校验。
    /// M2 真机修复 Bug 2：若 owner 是 IInteractiveHost（IconLayerWindow），ShowDialog 前临时激活 app
    /// 让 Input TextBox 可输入；用 try/finally 确保 EndInput 在 OK/Cancel/关窗/Esc 任一返回路径都被调到。</summary>
    public static string? AskRename(Window owner, string prompt, string currentName)
    {
        var dlg = new RenameDialog
        {
            Owner = owner,
            Prompt = { Text = prompt },
            Input = { Text = currentName }
        };
        // 选中主名（不含扩展名）；无扩展名则全选。放 Loaded 确保模板渲染后选中区可见。
        dlg.Loaded += (_, _) =>
        {
            var dot = currentName.LastIndexOf('.');
            dlg.Input.Select(0, dot > 0 ? dot : currentName.Length);
            dlg.Input.Focus();
        };
        // M2 真机修复 Bug 2：owner 是 NOACTIVATE 窗口时，Input TextBox 无法获取键盘焦点 →
        // 在 ShowDialog 前临时激活 app；try/finally 包 ShowDialog 保证任何返回路径都恢复 NOACTIVATE。
        IInteractiveHost? host = owner as IInteractiveHost;
        host?.BeginInput();
        try
        {
            return dlg.ShowDialog() == true ? dlg.Input.Text.Trim() : null;
        }
        finally
        {
            host?.EndInput();
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // Enter=确认（IsDefault 已覆盖，这里显式兜底）；Esc=取消（IsCancel 已覆盖）。
        if (e.Key == Key.Enter)
        {
            DialogResult = true;
            e.Handled = true;
        }
    }
}
