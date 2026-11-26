using Avalonia.Controls;
using Avalonia.Interactivity;
using YGORogueDuelistCardModInstaller.Avalonia.Services;

namespace YGORogueDuelistCardModInstaller.Avalonia.Views;

public partial class NewWorkspaceDialog : Window
{
    private readonly IDialogService? _dialogService;
    private readonly bool _isEnglish;

    public NewWorkspaceDialog() : this(null, false, null)
    {
    }

    public NewWorkspaceDialog(IDialogService? dialogService, bool isEnglish, string? defaultParentDir)
    {
        _dialogService = dialogService;
        _isEnglish = isEnglish;
        InitializeComponent();

        if (!string.IsNullOrWhiteSpace(defaultParentDir))
        {
            PathTextBox.Text = Path.Combine(defaultParentDir, "my-ygo-workspace");
        }

        if (_isEnglish)
        {
            Title = "New / Add MOD Workspace";
            SubtitleTextBlock.Text = "Create or register an isolated MOD workspace (with its own mods\\ and load order) to switch between modpacks or dev projects.";
            NameLabel.Text = "Workspace Name *";
            NameTextBox.Watermark = "e.g. Blue-Eyes Pack / Dev Sandbox";
            PathLabel.Text = "Workspace Folder Path *";
            BrowseButton.Content = "Browse...";
            DescLabel.Text = "Notes (optional)";
            DescTextBox.Watermark = "Describe this workspace...";
            CancelButton.Content = "Cancel";
            ConfirmButton.Content = "Save & Switch";
        }
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        if (_dialogService is null) return;
        var picked = await _dialogService.PickFolderAsync(
            _isEnglish ? "Select Workspace Folder" : "选择 MOD 工作区文件夹",
            PathTextBox.Text);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            PathTextBox.Text = picked;
            if (string.IsNullOrWhiteSpace(NameTextBox.Text))
            {
                NameTextBox.Text = Path.GetFileName(picked);
            }
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        var path = PathTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            ErrorTextBlock.Text = _isEnglish ? "Please enter or browse a workspace folder path." : "请输入或选择工作区文件夹路径。";
            ErrorTextBlock.IsVisible = true;
            return;
        }

        var name = string.IsNullOrWhiteSpace(NameTextBox.Text) ? Path.GetFileName(path) : NameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = "Workspace";
        var desc = DescTextBox.Text?.Trim() ?? string.Empty;
        Close(new NewWorkspaceInput(name, path, desc));
    }
}
