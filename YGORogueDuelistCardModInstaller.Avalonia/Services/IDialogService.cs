namespace YGORogueDuelistCardModInstaller.Avalonia.Services;

public sealed record CreateModInput(
    string Id,
    string Name,
    string Author,
    string Description,
    string? MinGameVersion = null);

public sealed record NewWorkspaceInput(
    string Name,
    string Path,
    string Description);

public interface IDialogService
{
    Task<string?> PickFolderAsync(string title, string? initialPath = null);
    Task<string?> PickZipFileAsync(string title, string? initialPath = null);
    Task<string?> PickSaveZipFileAsync(string title, string suggestedFileName, string? initialPath = null);
    Task<string?> PickImageFileAsync(string title, string? initialPath = null);
    Task<string?> PickYdkFileAsync(string title, string? initialPath = null);
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "继续", string cancelText = "取消");
    Task<CreateModInput?> PromptCreateModAsync(bool isEnglish = false, string? defaultMinGameVersion = null);
    Task<NewWorkspaceInput?> PromptNewWorkspaceAsync(bool isEnglish = false, string? defaultParentDir = null);
    Task CopyTextAsync(string text);
    void OpenFolderInExplorer(string path);
    void OpenUrlInBrowser(string url);
}
