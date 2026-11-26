using Avalonia.Threading;
using YGORogueDuelistCardModInstaller.Core;

namespace YGORogueDuelistCardModInstaller.Avalonia.Services;

public sealed class AvaloniaInstallerInteraction(
    Action<LogLevel, string> onLog,
    Action<string, double?> onProgress,
    IDialogService dialogService) : IInstallerInteraction
{
    public void Log(LogLevel level, string message)
    {
        Dispatcher.UIThread.Post(() => onLog(level, message));
    }

    public void ReportProgress(string status, double? percent = null)
    {
        Dispatcher.UIThread.Post(() => onProgress(status, percent));
    }

    public Task<bool> ConfirmAsync(string prompt, CancellationToken cancellationToken = default)
    {
        return Dispatcher.UIThread.InvokeAsync(() =>
            dialogService.ConfirmAsync("需要确认", prompt, "继续", "取消"));
    }

    public Task<string?> PromptForPicsDirAsync(string prompt, CancellationToken cancellationToken = default)
    {
        return Dispatcher.UIThread.InvokeAsync(() =>
            dialogService.PickFolderAsync("选择 YGOPro 卡图目录（取消则跳过）"));
    }
}
