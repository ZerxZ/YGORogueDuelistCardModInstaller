using Avalonia.Controls;
using Avalonia.Interactivity;

namespace YGORogueDuelistCardModInstaller.Avalonia.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog() : this("确认", string.Empty, "确定", "取消")
    {
    }

    public ConfirmDialog(string title, string message, string confirmText, string cancelText)
    {
        InitializeComponent();
        Title = title;
        MessageTextBlock.Text = message;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
