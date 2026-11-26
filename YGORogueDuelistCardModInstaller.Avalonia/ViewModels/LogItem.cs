using Avalonia.Media;
using Avalonia.Media.Immutable;
using YGORogueDuelistCardModInstaller.Core;

namespace YGORogueDuelistCardModInstaller.Avalonia.ViewModels;

public sealed class LogItem
{
    private static readonly IBrush InfoBrush = new ImmutableSolidColorBrush(Color.Parse("#D6DCE8"));
    private static readonly IBrush OkBrush = new ImmutableSolidColorBrush(Color.Parse("#4ADE80"));
    private static readonly IBrush WarnBrush = new ImmutableSolidColorBrush(Color.Parse("#FACC15"));
    private static readonly IBrush ErrorBrush = new ImmutableSolidColorBrush(Color.Parse("#F87171"));

    public LogItem(LogLevel level, string message)
    {
        TimeText = DateTime.Now.ToString("HH:mm:ss");
        Level = level;
        Message = message;
        Prefix = level switch
        {
            LogLevel.Ok => "[OK] ",
            LogLevel.Warn => "[注意] ",
            LogLevel.Error => "[失败] ",
            _ => string.Empty
        };
        Foreground = level switch
        {
            LogLevel.Ok => OkBrush,
            LogLevel.Warn => WarnBrush,
            LogLevel.Error => ErrorBrush,
            _ => InfoBrush
        };
    }

    public string TimeText { get; }
    public LogLevel Level { get; }
    public string Prefix { get; }
    public string Message { get; }
    public IBrush Foreground { get; }

    public string FullText => $"[{TimeText}] {Prefix}{Message}";
}
