namespace YGORogueDuelistCardModInstaller.Core;

public interface IInstallerInteraction
{
    void Log(LogLevel level, string message);
    void ReportProgress(string status, double? percent = null);
    Task<bool> ConfirmAsync(string prompt, CancellationToken cancellationToken = default);
    Task<string?> PromptForPicsDirAsync(string prompt, CancellationToken cancellationToken = default);
}

public static class InstallerInteractionExtensions
{
    public static void Say(this IInstallerInteraction interaction, string message) =>
        interaction.Log(LogLevel.Info, message);

    public static void Ok(this IInstallerInteraction interaction, string message) =>
        interaction.Log(LogLevel.Ok, message);

    public static void Warn(this IInstallerInteraction interaction, string message) =>
        interaction.Log(LogLevel.Warn, message);

    public static void Error(this IInstallerInteraction interaction, string message) =>
        interaction.Log(LogLevel.Error, message);
}
