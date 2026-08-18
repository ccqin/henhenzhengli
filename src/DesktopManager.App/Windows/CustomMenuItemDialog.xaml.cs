using System.Windows;
using DesktopManager.Core.Models;

namespace DesktopManager.App.Windows;

/// <summary>自定义右键菜单项编辑对话框（名称/命令/扩展名过滤）。</summary>
public partial class CustomMenuItemDialog : Window
{
    public CustomMenuItemDialog(Window owner)
    {
        Owner = owner;
        InitializeComponent();
    }

    /// <summary>编辑模式的初始值（确定后更新为 Result）。</summary>
    public CustomMenuItem Result
    {
        get => new() { Name = NameBox.Text.Trim(), Command = CommandBox.Text.Trim(), Extensions = ExtBox.Text.Trim() };
        init
        {
            NameBox.Text = value.Name;
            CommandBox.Text = value.Command;
            ExtBox.Text = value.Extensions;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text) || string.IsNullOrWhiteSpace(CommandBox.Text))
        {
            MessageBox.Show(this, "名称和命令不能为空", "自定义菜单项", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
