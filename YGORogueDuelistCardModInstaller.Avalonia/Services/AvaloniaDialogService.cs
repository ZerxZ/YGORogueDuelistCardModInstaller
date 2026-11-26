using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using YGORogueDuelistCardModInstaller.Avalonia.Views;

namespace YGORogueDuelistCardModInstaller.Avalonia.Services;

public sealed class AvaloniaDialogService(Window owner) : IDialogService
{
    public async Task<string?> PickFolderAsync(string title, string? initialPath = null)
    {
        var startFolder = await TryResolveFolderAsync(initialPath, createIfMissing: false);
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = startFolder
        });

        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickZipFileAsync(string title, string? initialPath = null)
    {
        var startFolder = await TryResolveFolderAsync(initialPath, createIfMissing: false);
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = startFolder,
            FileTypeFilter =
            [
                new FilePickerFileType("ZIP Archive (*.zip)") { Patterns = ["*.zip"] },
                FilePickerFileTypes.All
            ]
        });

        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickSaveZipFileAsync(string title, string suggestedFileName, string? initialPath = null)
    {
        // Always ensure dist/ exists so Windows Save Dialog defaults to workspace dist\ instead of OneDrive\Documents
        var startFolder = await TryResolveFolderAsync(initialPath, createIfMissing: true);
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "zip",
            SuggestedStartLocation = startFolder,
            FileTypeChoices =
            [
                new FilePickerFileType("ZIP Archive (*.zip)") { Patterns = ["*.zip"] }
            ]
        });

        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickImageFileAsync(string title, string? initialPath = null)
    {
        var startFolder = await TryResolveFolderAsync(initialPath, createIfMissing: false);
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = startFolder,
            FileTypeFilter =
            [
                new FilePickerFileType("Image (*.png;*.jpg;*.jpeg;*.webp)") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"] },
                FilePickerFileTypes.All
            ]
        });

        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickYdkFileAsync(string title, string? initialPath = null)
    {
        var startFolder = await TryResolveFolderAsync(initialPath, createIfMissing: false);
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = startFolder,
            FileTypeFilter =
            [
                new FilePickerFileType("YGOPro Deck (*.ydk)") { Patterns = ["*.ydk"] },
                FilePickerFileTypes.All
            ]
        });

        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "继续", string cancelText = "取消")
    {
        var dialog = new ConfirmDialog(title, message, confirmText, cancelText);
        return await dialog.ShowDialog<bool>(owner);
    }

    public async Task<CreateModInput?> PromptCreateModAsync(bool isEnglish = false, string? defaultMinGameVersion = null)
    {
        var dialog = new CreateModDialog(isEnglish, defaultMinGameVersion);
        return await dialog.ShowDialog<CreateModInput?>(owner);
    }

    public async Task<NewWorkspaceInput?> PromptNewWorkspaceAsync(bool isEnglish = false, string? defaultParentDir = null)
    {
        var dialog = new NewWorkspaceDialog(this, isEnglish, defaultParentDir);
        return await dialog.ShowDialog<NewWorkspaceInput?>(owner);
    }

    public async Task CopyTextAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(owner)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    public void OpenFolderInExplorer(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{Path.GetFullPath(path)}\"",
            UseShellExecute = true
        });
    }

    public void OpenUrlInBrowser(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    private async Task<IStorageFolder?> TryResolveFolderAsync(string? initialPath, bool createIfMissing)
    {
        if (string.IsNullOrWhiteSpace(initialPath)) return null;

        try
        {
            var full = Path.GetFullPath(initialPath);
            if (createIfMissing && !Directory.Exists(full))
            {
                Directory.CreateDirectory(full);
            }

            if (!Directory.Exists(full)) return null;
            return await owner.StorageProvider.TryGetFolderFromPathAsync(full);
        }
        catch
        {
            return null;
        }
    }
}
