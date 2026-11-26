using YGORogueDuelistCardModInstaller.Core;

namespace YGORogueDuelistCardModInstaller;

public sealed class ConsoleInteraction : IInstallerInteraction
{
    private readonly Lock _consoleLock = new();

    public void Log(LogLevel level, string message)
    {
        lock (_consoleLock)
        {
            var prev = Console.ForegroundColor;
            try
            {
                switch (level)
                {
                    case LogLevel.Info:
                        Console.WriteLine($"  {message}");
                        break;
                    case LogLevel.Ok:
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"  [OK] {message}");
                        break;
                    case LogLevel.Warn:
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"  [注意] {message}");
                        break;
                    case LogLevel.Error:
                        Console.WriteLine();
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"  [失败] {message}");
                        Console.WriteLine();
                        break;
                }
            }
            finally
            {
                Console.ForegroundColor = prev;
            }
        }
    }

    public void ReportProgress(string status, double? percent = null)
    {
        // CLI relies on detailed step logs; no noisy progress bar overwrites.
    }

    public Task<bool> ConfirmAsync(string prompt, CancellationToken cancellationToken = default)
    {
        if (Console.IsInputRedirected)
        {
            return Task.FromResult(true);
        }

        lock (_consoleLock)
        {
            Console.Write("  继续吗？(y/N): ");
            var answer = Console.ReadLine()?.Trim();
            return Task.FromResult(string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase));
        }
    }

    public Task<string?> PromptForPicsDirAsync(string prompt, CancellationToken cancellationToken = default)
    {
        if (Console.IsInputRedirected)
        {
            return Task.FromResult<string?>(null);
        }

        lock (_consoleLock)
        {
            Console.Write("  输入卡图目录（直接回车跳过）: ");
            var answer = Console.ReadLine()?.Trim();
            return Task.FromResult(string.IsNullOrWhiteSpace(answer) ? null : answer);
        }
    }
}
