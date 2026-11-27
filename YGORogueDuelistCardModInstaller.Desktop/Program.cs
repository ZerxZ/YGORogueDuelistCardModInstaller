using System.Runtime.InteropServices;
using Photino.NET;

namespace YGORogueDuelistCardModInstaller.Desktop;

internal static class Program
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;

    private const int SW_RESTORE = 9;
    private const int SW_MAXIMIZE = 3;
    private const int SW_MINIMIZE = 6;

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left, Right, Top, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetSaveFileNameW(ref OPENFILENAME ofn);

    private static string? ShowWindowsSaveArchiveDialog(IntPtr ownerHwnd, string title, string suggestedName, string? initialDir)
    {
        string? picked = null;
        var thread = new Thread(() =>
        {
            picked = ShowWindowsSaveArchiveDialogCore(ownerHwnd, title, suggestedName, initialDir);
        });
        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.STA);
        }
        thread.Start();
        thread.Join();
        return picked;
    }

    private static string? ShowWindowsSaveArchiveDialogCore(IntPtr ownerHwnd, string title, string suggestedName, string? initialDir)
    {
        const int OFN_OVERWRITEPROMPT = 0x00000002;
        const int OFN_NOCHANGEDIR = 0x00000008;
        const int OFN_PATHMUSTEXIST = 0x00000800;
        const int OFN_EXPLORER = 0x00080000;
        const int maxChars = 4096;

        var preferZip = suggestedName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        var cleanTitle = title.Replace("/", " ").Replace("\\", " ");
        var buf = Marshal.AllocHGlobal(maxChars * 2);
        try
        {
            // Zero-fill and copy suggested filename into Unicode buffer
            for (var i = 0; i < maxChars * 2; i++) Marshal.WriteByte(buf, i, 0);
            var safeName = Path.GetFileName(suggestedName);
            if (!string.IsNullOrWhiteSpace(safeName))
            {
                var chars = safeName.ToCharArray();
                Marshal.Copy(chars, 0, buf, Math.Min(chars.Length, maxChars - 1));
            }

            var ofn = new OPENFILENAME
            {
                lStructSize = Marshal.SizeOf<OPENFILENAME>(),
                hwndOwner = ownerHwnd,
                lpstrFilter = "YGO Rogue MOD Package (*.yrdpk)\0*.yrdpk\0ZIP Archive (*.zip)\0*.zip\0All Files (*.*)\0*.*\0\0",
                nFilterIndex = preferZip ? 2 : 1,
                lpstrFile = buf,
                nMaxFile = maxChars,
                lpstrInitialDir = !string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir) ? initialDir : null,
                lpstrTitle = cleanTitle,
                Flags = OFN_EXPLORER | OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
                lpstrDefExt = preferZip ? "zip" : "yrdpk"
            };

            if (!GetSaveFileNameW(ref ofn)) return null;
            var result = Marshal.PtrToStringUni(buf)?.Trim();
            if (string.IsNullOrWhiteSpace(result)) return null;
            if (!result.EndsWith(".yrdpk", StringComparison.OrdinalIgnoreCase) &&
                !result.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                result += ofn.nFilterIndex == 2 ? ".zip" : ".yrdpk";
            }
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static void ApplyWindows11Chrome(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return;
        try
        {
            var dark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

            var corner = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

            // 1px bottom margin preserves native Windows 11 DWM drop shadow on chromeless windows
            var margins = new MARGINS { Left = 0, Right = 0, Top = 0, Bottom = 1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);
        }
        catch
        {
            // Ignore on older Windows versions
        }
    }

    private static void BeginNativeDragOrResize(PhotinoWindow window, int hitTest)
    {
        if (!OperatingSystem.IsWindows()) return;
        var hwnd = window.WindowHandle;
        if (hwnd == IntPtr.Zero) return;
        ReleaseCapture();
        PostMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)hitTest, IntPtr.Zero);
    }

    private static bool ToggleMaximize(PhotinoWindow window)
    {
        if (OperatingSystem.IsWindows())
        {
            var hwnd = window.WindowHandle;
            if (hwnd != IntPtr.Zero)
            {
                var currentlyMax = IsZoomed(hwnd);
                ShowWindow(hwnd, currentlyMax ? SW_RESTORE : SW_MAXIMIZE);
                return !currentlyMax;
            }
        }
        var next = !window.Maximized;
        window.SetMaximized(next);
        return next;
    }

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Any(a => a.Equals("--mcp", StringComparison.OrdinalIgnoreCase) || a.Equals("--stdio-mcp", StringComparison.OrdinalIgnoreCase)))
        {
            using var mcpCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; mcpCts.Cancel(); };
            YGORogueDuelistCardModInstaller.Core.Mcp.McpProtocolHandler
                .RunStdioLoopAsync(hooks: null, ct: mcpCts.Token)
                .GetAwaiter()
                .GetResult();
            return;
        }

        PhotinoWindow? window = null;

        using var apiServer = new DesktopApiServer(
            pickFolderOnUi: (title, initialPath) =>
            {
                if (window is null) return null;
                var result = window.ShowOpenFolder(title, initialPath, multiSelect: false);
                return result is { Length: > 0 } ? result[0] : null;
            },
            pickOpenFileOnUi: (title, filters) =>
            {
                if (window is null) return null;
                var result = window.ShowOpenFile(title, defaultPath: null, multiSelect: false, filters: filters);
                return result is { Length: > 0 } ? result[0] : null;
            },
            pickSaveZipOnUi: (title, suggestedName, initialPath) =>
            {
                if (window is null)
                {
                    // Headless / browser --serve mode: pack straight into dist/<suggestedName>
                    return string.IsNullOrWhiteSpace(initialPath)
                        ? suggestedName
                        : Path.Combine(initialPath, suggestedName);
                }
                if (OperatingSystem.IsWindows())
                {
                    // Use native Win32 GetSaveFileNameW on a dedicated STA thread (100% Native AOT-safe, full Unicode/Chinese filename + .yrdpk/.zip support!)
                    return ShowWindowsSaveArchiveDialog(window.WindowHandle, title, suggestedName, initialPath);
                }
                var defaultPath = string.IsNullOrWhiteSpace(initialPath)
                    ? suggestedName
                    : Path.Combine(initialPath, suggestedName);
                return window.ShowSaveFile(title, defaultPath, filters:
                [
                    ("YGO Rogue MOD Package (.yrdpk)", ["*.yrdpk"]),
                    ("ZIP Archive (.zip)", ["*.zip"])
                ]);
            });

        apiServer.Start(17850);

        // Support headless API/WebUI-only mode for automated testing, Linux servers, or Android Termux (`--api-only` / `--serve`)
        if (args.Any(a =>
            string.Equals(a, "--api-only", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "--serve", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"API_URL={apiServer.BaseUrl}");
            Console.WriteLine($"Web Studio UI: {apiServer.BaseUrl}");
            Console.Out.Flush();
            using var ev = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; ev.Set(); };
            ev.Wait();
            return;
        }

        // Allow live Vite dev server override via YGO_DEV_URL=http://localhost:5173
        var devUrl = Environment.GetEnvironmentVariable("YGO_DEV_URL");
        var entryUrl = !string.IsNullOrWhiteSpace(devUrl)
            ? $"{devUrl.TrimEnd('/')}/?api={Uri.EscapeDataString(apiServer.BaseUrl.TrimEnd('/'))}"
            : apiServer.BaseUrl;

        window = new PhotinoWindow()
            .SetTitle("Yu-Gi-Oh! Rogue Duelist MOD Studio")
            .SetChromeless(OperatingSystem.IsWindows())
            .SetUseOsDefaultSize(false)
            .SetUseOsDefaultLocation(false)
            .SetSize(1560, 940)
            .SetMinSize(1080, 680)
            .Center()
            .SetResizable(true)
            .SetContextMenuEnabled(true)
            .SetDevToolsEnabled(true)
            .RegisterWindowCreatedHandler((sender, _) =>
            {
                if (OperatingSystem.IsWindows() && sender is PhotinoWindow w)
                {
                    ApplyWindows11Chrome(w.WindowHandle);
                }
            })
            .RegisterWebMessageReceivedHandler((sender, message) =>
            {
                if (sender is not PhotinoWindow w || string.IsNullOrWhiteSpace(message)) return;
                switch (message)
                {
                    case "win:drag":
                        BeginNativeDragOrResize(w, HTCAPTION);
                        break;
                    case "win:minimize":
                        if (OperatingSystem.IsWindows() && w.WindowHandle != IntPtr.Zero) ShowWindow(w.WindowHandle, SW_MINIMIZE);
                        else w.SetMinimized(true);
                        break;
                    case "win:maximize":
                    {
                        var isMax = ToggleMaximize(w);
                        w.SendWebMessage(isMax ? "win:state:maximized" : "win:state:normal");
                        break;
                    }
                    case "win:close":
                        w.Close();
                        break;
                    case "win:resize-n": BeginNativeDragOrResize(w, HTTOP); break;
                    case "win:resize-s": BeginNativeDragOrResize(w, HTBOTTOM); break;
                    case "win:resize-e": BeginNativeDragOrResize(w, HTRIGHT); break;
                    case "win:resize-w": BeginNativeDragOrResize(w, HTLEFT); break;
                    case "win:resize-ne": BeginNativeDragOrResize(w, HTTOPRIGHT); break;
                    case "win:resize-nw": BeginNativeDragOrResize(w, HTTOPLEFT); break;
                    case "win:resize-se": BeginNativeDragOrResize(w, HTBOTTOMRIGHT); break;
                    case "win:resize-sw": BeginNativeDragOrResize(w, HTBOTTOMLEFT); break;
                }
            })
            .Load(entryUrl);

        window.WaitForClose();
    }
}
