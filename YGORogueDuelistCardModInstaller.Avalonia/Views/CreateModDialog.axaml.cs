using Avalonia.Controls;
using Avalonia.Interactivity;
using YGORogueDuelistCardModInstaller.Avalonia.Services;

namespace YGORogueDuelistCardModInstaller.Avalonia.Views;

public partial class CreateModDialog : Window
{
    private readonly bool _isEnglish;

    public CreateModDialog() : this(false, null)
    {
    }

    public CreateModDialog(bool isEnglish, string? defaultMinGameVersion)
    {
        _isEnglish = isEnglish;
        InitializeComponent();
        AuthorTextBox.Text = Environment.UserName;
        if (!string.IsNullOrWhiteSpace(defaultMinGameVersion))
        {
            MinGameVerTextBox.Text = defaultMinGameVersion;
        }

        if (_isEnglish)
        {
            Title = "New MOD Template";
            SubtitleTextBlock.Text = "Create a Mod Spec v1 template folder under mods\\ (supports mod.lua/mod.js DSL)";
            IdLabel.Text = "MOD ID (unique slug, e.g. my-blue-eyes-pack) *";
            NameLabel.Text = "Display Name";
            NameTextBox.Watermark = "My Custom Expansion MOD";
            AuthorLabel.Text = "Author";
            AuthorTextBox.Watermark = "Your name";
            MinVerLabel.Text = "Min Game Version (opt.)";
            DescLabel.Text = "Description";
            DescTextBox.Watermark = "Describe what this MOD adds...";
            CancelButton.Content = "Cancel";
            CreateButton.Content = "Create MOD";
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnCreateClick(object? sender, RoutedEventArgs e)
    {
        var id = IdTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id))
        {
            ErrorTextBlock.Text = _isEnglish ? "Please enter a MOD ID." : "请输入 MOD ID。";
            ErrorTextBlock.IsVisible = true;
            return;
        }

        var name = string.IsNullOrWhiteSpace(NameTextBox.Text) ? id : NameTextBox.Text.Trim();
        var author = AuthorTextBox.Text?.Trim() ?? string.Empty;
        var desc = DescTextBox.Text?.Trim() ?? string.Empty;
        var minVer = string.IsNullOrWhiteSpace(MinGameVerTextBox.Text) ? null : MinGameVerTextBox.Text.Trim();
        Close(new CreateModInput(id, name, author, desc, minVer));
    }
}
