using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace YGORogueDuelistCardModInstaller.Core.ModManager;

public sealed record HotReloadEventInfo(
    int Revision,
    bool CssOnly,
    IReadOnlyList<string> ChangedFiles,
    int AppliedModCount,
    DateTimeOffset TimestampUtc,
    string? Error);

/// <summary>
/// Live Hot-Reload Dev Server + FileSystemWatcher for Yu-Gi-Oh! Rogue Duelist MODs.
/// - Watches workspace mods/ with debounced automatic in-memory re-composition (~200ms).
/// - Streams SSE events on /__hotreload/events to both the real desktop game (via mod-hot-reload.js)
///   and any browser preview opened at http://127.0.0.1:&lt;port&gt;/.
/// - Serves freshly composed files from /__hotreload/files/&lt;path&gt; with Cache-Control: no-store,
///   so CSS hot-swaps without reload and Cards/Lua/JS/Characters/Relics/Packs/Decks reload in ~1s
///   without restarting the game!
/// 100% Native AOT compatible.
/// </summary>
public sealed class ModHotReloadServer : IDisposable
{
    public const int DefaultPort = 17845;
    public const string HotReloadClientScriptPath = "mod-hot-reload.js";

    private readonly ModInjectionEngine _engine;
    private readonly Action<LogLevel, string>? _log;
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<Guid, SseClient> _sseClients = new();
    private readonly HashSet<string> _pendingChangedFiles = new(StringComparer.OrdinalIgnoreCase);

    private HttpListener? _listener;
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    private string? _vanillaZipPath;
    private Dictionary<string, byte[]> _composedFiles = new(StringComparer.OrdinalIgnoreCase);
    private int _revision;
    private int _appliedModCount;
    private DateTimeOffset _lastReloadUtc = DateTimeOffset.UtcNow;
    private string? _lastError;
    private bool _disposed;

    public ModHotReloadServer(ModInjectionEngine engine, Action<LogLevel, string>? log = null)
    {
        _engine = engine;
        _log = log;
    }

    public int Port { get; private set; }
    public bool IsRunning => _listener is { IsListening: true };
    public bool LanBound { get; private set; }
    public int Revision => _revision;
    public string BaseUrl => $"http://127.0.0.1:{Port}/";
    public IReadOnlyList<string> LanUrls => IsRunning ? GetLocalLanUrls(Port) : [];
    public string? LastError => _lastError;

    public event Action<HotReloadEventInfo>? Reloaded;

    public async Task StartAsync(int preferredPort = DefaultPort, CancellationToken ct = default, bool allowLan = true)
    {
        if (IsRunning) return;

        var status = _engine.GetStatus();
        var vanilla = ModInjectionEngine.FindBestVanillaSource(status.InstallDir, status.GameBaseDir, status.LauncherMeta)
            ?? throw new ModOperationException("找不到原版游戏 web-X.Y.Z.zip，无法启动热加载服务器。请先在设置中指定游戏安装目录。");
        _vanillaZipPath = vanilla.ZipPath;

        // Try LAN-capable binding first (allows Android/Linux devices on same Wi-Fi to play live PWA!), fallback cleanly to loopback
        HttpListener? listener = null;
        var chosenPort = preferredPort;
        var lanOk = false;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var candidatePort = preferredPort + attempt;
            if (allowLan)
            {
                var lanCandidate = new HttpListener();
                lanCandidate.Prefixes.Add($"http://+:{candidatePort}/");
                try
                {
                    lanCandidate.Start();
                    listener = lanCandidate;
                    chosenPort = candidatePort;
                    lanOk = true;
                    break;
                }
                catch
                {
                    lanCandidate.Close();
                }
            }

            var candidate = new HttpListener();
            candidate.Prefixes.Add($"http://127.0.0.1:{candidatePort}/");
            try
            {
                candidate.Start();
                listener = candidate;
                chosenPort = candidatePort;
                lanOk = false;
                break;
            }
            catch
            {
                candidate.Close();
            }
        }

        if (listener is null)
        {
            throw new ModOperationException($"无法在本地端口 {preferredPort}-{preferredPort + 19} 启动热加载服务器。");
        }

        Port = chosenPort;
        LanBound = lanOk;
        _listener = listener;
        _cts = new CancellationTokenSource();

        // Perform initial composition
        await RebuildInMemoryAsync(["(initial)"], ct);

        // Start FileSystemWatcher on workspace mods/
        Directory.CreateDirectory(_engine.Repository.ModsRoot);
        _watcher = new FileSystemWatcher(_engine.Repository.ModsRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        _watcher.Changed += OnFsChanged;
        _watcher.Created += OnFsChanged;
        _watcher.Deleted += OnFsChanged;
        _watcher.Renamed += OnFsRenamed;
        _watcher.EnableRaisingEvents = true;

        _listenTask = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
        var lanHint = lanOk && LanUrls.Count > 0 ? $" · 局域网/安卓 PWA：{LanUrls[0]}" : "";
        _log?.Invoke(LogLevel.Ok, $"热加载服务器已启动：{BaseUrl}{lanHint}（监听 {_engine.Repository.ModsRoot}）");
    }

    private static List<string> GetLocalLanUrls(int port)
    {
        var urls = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = ua.Address.ToString();
                    if (ip.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                    urls.Add($"http://{ip}:{port}/");
                }
            }
        }
        catch { }
        return urls;
    }

    public void Stop()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        _debounceTimer?.Dispose();
        _debounceTimer = null;

        _cts?.Cancel();
        foreach (var kv in _sseClients)
        {
            kv.Value.Dispose();
        }
        _sseClients.Clear();

        if (_listener is not null)
        {
            try { _listener.Stop(); _listener.Close(); } catch { }
            _listener = null;
        }

        _cts?.Dispose();
        _cts = null;
        _log?.Invoke(LogLevel.Info, "热加载服务器已停止。");
    }

    public async Task<HotReloadEventInfo> TriggerManualReloadAsync(string? reasonFile = null, CancellationToken ct = default)
    {
        var files = string.IsNullOrWhiteSpace(reasonFile) ? new[] { "(manual)" } : new[] { reasonFile };
        return await RebuildInMemoryAsync(files, ct);
    }

    private void OnFsChanged(object sender, FileSystemEventArgs e) => QueueFileChange(e.FullPath);
    private void OnFsRenamed(object sender, RenamedEventArgs e) => QueueFileChange(e.FullPath);

    private void QueueFileChange(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return;
        var name = Path.GetFileName(fullPath);
        if (name.StartsWith('.') || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith("~", StringComparison.Ordinal))
            return;

        var rel = Path.GetRelativePath(_engine.Repository.ModsRoot, fullPath).Replace('\\', '/');
        lock (_sync)
        {
            _pendingChangedFiles.Add(rel);
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => _ = FlushPendingChangesAsync(), null, 350, Timeout.Infinite);
        }
    }

    private async Task FlushPendingChangesAsync()
    {
        string[] batch;
        lock (_sync)
        {
            if (_pendingChangedFiles.Count == 0) return;
            batch = _pendingChangedFiles.ToArray();
            _pendingChangedFiles.Clear();
        }

        if (!IsRunning) return;
        await RebuildInMemoryAsync(batch, _cts?.Token ?? CancellationToken.None);
    }

    private async Task<HotReloadEventInfo> RebuildInMemoryAsync(IReadOnlyList<string> changedFiles, CancellationToken ct)
    {
        var cssOnly = changedFiles.Count > 0 &&
                      changedFiles.All(f => f.EndsWith(".css", StringComparison.OrdinalIgnoreCase));

        try
        {
            var state = _engine.Repository.LoadState();
            var mods = _engine.Repository.LoadMods(state);
            var picsDir = state.PicsDir ?? GameLocator.FindPicsDir();
            var silent = new SilentLogInteraction(_log);

            var composed = await Task.Run(() => ModCompositor.Compose(
                _vanillaZipPath!,
                mods,
                picsDir,
                silent,
                ct,
                developerMode: state.DeveloperMode,
                hotReloadPort: Port,
                hideOnlineWhenModded: state.HideOnlineWhenModded,
                enableBuiltInTrainer: state.EnableBuiltInTrainer || state.DeveloperMode), ct);

            var nextMap = new Dictionary<string, byte[]>(composed.Files, StringComparer.OrdinalIgnoreCase);

            // Ensure index.html always has hot-reload bridge & CSP configured even when 0 JS mods exist
            if (!nextMap.ContainsKey("index.html"))
            {
                using var zip = ZipFile.OpenRead(_vanillaZipPath!);
                var rawHtml = ModPatcher.ReadZipText(zip, "index.html");
                nextMap["index.html"] = ModPatcher.GetUtf8Bytes(PatchIndexHtmlForHotReload(rawHtml, Port));
            }
            if (!nextMap.ContainsKey(HotReloadClientScriptPath))
            {
                nextMap[HotReloadClientScriptPath] = ModPatcher.GetUtf8Bytes(BuildHotReloadClientScript(Port));
            }

            int rev;
            lock (_sync)
            {
                _composedFiles = nextMap;
                _revision++;
                rev = _revision;
                _appliedModCount = composed.AppliedModCount;
                _lastReloadUtc = DateTimeOffset.UtcNow;
                _lastError = null;
            }

            var info = new HotReloadEventInfo(rev, cssOnly, changedFiles, composed.AppliedModCount, _lastReloadUtc, null);
            _log?.Invoke(LogLevel.Ok, $"[热加载 Rev #{rev}] 合成完成（{composed.AppliedModCount} 个 MOD，{nextMap.Count} 个覆盖文件，{(cssOnly ? "CSS 无刷新热更新" : "触发实时刷新")}）");
            BroadcastSseEvent("reload", SerializeEvent(info));
            Reloaded?.Invoke(info);
            return info;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            var errInfo = new HotReloadEventInfo(_revision, false, changedFiles, _appliedModCount, DateTimeOffset.UtcNow, ex.Message);
            _log?.Invoke(LogLevel.Error, $"[热加载失败] {ex.Message}");
            BroadcastSseEvent("error", SerializeEvent(errInfo));
            Reloaded?.Invoke(errInfo);
            return errInfo;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(ct);
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleRequestAsync(ctx, ct), ct);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        res.Headers["Access-Control-Allow-Origin"] = "*";
        res.Headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";
        res.Headers["Access-Control-Allow-Headers"] = "Content-Type, Cache-Control";

        if (string.Equals(req.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            res.StatusCode = 204;
            res.Close();
            return;
        }

        var path = Uri.UnescapeDataString(req.Url?.AbsolutePath ?? "/");

        if (string.Equals(path, "/__hotreload/events", StringComparison.OrdinalIgnoreCase))
        {
            await HandleSseAsync(res, ct);
            return;
        }

        if (string.Equals(path, "/__hotreload/status", StringComparison.OrdinalIgnoreCase))
        {
            var payload = BuildStatusJson();
            var bytes = ModPatcher.GetUtf8Bytes(payload);
            res.ContentType = "application/json; charset=utf-8";
            res.Headers["Cache-Control"] = "no-store";
            res.ContentLength64 = bytes.Length;
            await res.OutputStream.WriteAsync(bytes, ct);
            res.Close();
            return;
        }

        // Map /__hotreload/files/<rel> or /<rel>
        string relPath;
        if (path.StartsWith("/__hotreload/files/", StringComparison.OrdinalIgnoreCase))
        {
            relPath = path["/__hotreload/files/".Length..].TrimStart('/');
        }
        else
        {
            relPath = path.TrimStart('/');
            if (string.IsNullOrWhiteSpace(relPath)) relPath = "index.html";
        }

        if (relPath.Contains("..", StringComparison.Ordinal))
        {
            res.StatusCode = 403;
            res.Close();
            return;
        }

        // Disable Service Worker registration when previewing directly over hot-reload HTTP
        if (string.Equals(relPath, "sw.js", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/__hotreload/files/", StringComparison.OrdinalIgnoreCase))
        {
            var noopSw = ModPatcher.GetUtf8Bytes("self.addEventListener('install',()=>self.skipWaiting());self.addEventListener('activate',e=>e.waitUntil(self.clients.claim()));");
            res.ContentType = "application/javascript; charset=utf-8";
            res.Headers["Cache-Control"] = "no-store";
            res.ContentLength64 = noopSw.Length;
            await res.OutputStream.WriteAsync(noopSw, ct);
            res.Close();
            return;
        }

        byte[]? data = null;
        lock (_sync)
        {
            _composedFiles.TryGetValue(relPath, out data);
        }

        if (data is null && !string.IsNullOrWhiteSpace(_vanillaZipPath) && File.Exists(_vanillaZipPath))
        {
            data = TryReadVanillaZipEntry(_vanillaZipPath, relPath);
        }

        if (data is null)
        {
            res.StatusCode = 404;
            res.Close();
            return;
        }

        res.ContentType = GetMimeType(relPath);
        res.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        res.ContentLength64 = data.Length;
        try
        {
            await res.OutputStream.WriteAsync(data, ct);
        }
        catch
        {
            // Client disconnected mid-response
        }
        finally
        {
            try { res.Close(); } catch { }
        }
    }

    private async Task HandleSseAsync(HttpListenerResponse res, CancellationToken ct)
    {
        res.StatusCode = 200;
        res.ContentType = "text/event-stream; charset=utf-8";
        res.Headers["Cache-Control"] = "no-cache";
        res.Headers["Connection"] = "keep-alive";

        var id = Guid.NewGuid();
        var client = new SseClient(res);
        _sseClients[id] = client;

        try
        {
            await client.SendAsync("hello", BuildStatusJson(), ct);
            while (!ct.IsCancellationRequested && !client.IsClosed)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
                await client.SendCommentAsync("ping", ct);
            }
        }
        catch
        {
            // Disconnect
        }
        finally
        {
            _sseClients.TryRemove(id, out _);
            client.Dispose();
        }
    }

    private void BroadcastSseEvent(string eventName, string jsonData)
    {
        foreach (var (id, client) in _sseClients)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await client.SendAsync(eventName, jsonData, CancellationToken.None);
                }
                catch
                {
                    _sseClients.TryRemove(id, out _);
                    client.Dispose();
                }
            });
        }
    }

    private string BuildStatusJson()
    {
        string[] overlayKeys;
        lock (_sync)
        {
            overlayKeys = _composedFiles.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        var obj = new JsonObject
        {
            ["running"] = IsRunning,
            ["port"] = Port,
            ["rev"] = _revision,
            ["modCount"] = _appliedModCount,
            ["workspace"] = _engine.Repository.WorkspaceRoot,
            ["lastReloadUtc"] = _lastReloadUtc.ToString("O"),
            ["error"] = _lastError,
            ["files"] = new JsonArray(overlayKeys.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray())
        };
        return obj.ToJsonString(ModPatcher.JsonOptions);
    }

    private static string SerializeEvent(HotReloadEventInfo info)
    {
        var obj = new JsonObject
        {
            ["rev"] = info.Revision,
            ["cssOnly"] = info.CssOnly,
            ["modCount"] = info.AppliedModCount,
            ["timestampUtc"] = info.TimestampUtc.ToString("O"),
            ["error"] = info.Error,
            ["changedFiles"] = new JsonArray(info.ChangedFiles.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray())
        };
        return obj.ToJsonString(ModPatcher.JsonOptions);
    }

    public static string PatchIndexHtmlForHotReload(string indexHtml, int port)
    {
        var origin = $"http://127.0.0.1:{port}";
        var html = indexHtml;

        // Widen CSP so the real desktop WebView2 app can connect to the local loopback hot-reload server
        if (html.Contains("Content-Security-Policy", StringComparison.OrdinalIgnoreCase) &&
            !html.Contains(origin, StringComparison.OrdinalIgnoreCase))
        {
            html = Regex.Replace(html, @"(connect-src\s+[^;""]+)", $"$1 {origin}", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"(script-src\s+[^;""]+)", $"$1 {origin}", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"(style-src\s+[^;""]+)", $"$1 {origin}", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"(img-src\s+[^;""]+)", $"$1 {origin}", RegexOptions.IgnoreCase);
        }

        const string tag = $"<script type=\"module\" src=\"{HotReloadClientScriptPath}\"></script>";
        if (!html.Contains(HotReloadClientScriptPath, StringComparison.OrdinalIgnoreCase))
        {
            var headIdx = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            if (headIdx >= 0)
            {
                html = html.Insert(headIdx, tag);
            }
            else
            {
                html += tag;
            }
        }

        return html;
    }

    public static string BuildHotReloadClientScript(int port) => $$"""
        // Yu-Gi-Oh! Rogue Duelist Live MOD Hot-Reload Client (Auto-generated)
        const HOT_ORIGIN = 'http://127.0.0.1:{{port}}';
        let currentRev = Number(sessionStorage.getItem('ygo_hot_rev') || '0');
        let autoReload = sessionStorage.getItem('ygo_hot_auto') !== '0';
        let liveFiles = new Set();

        async function syncLiveFileList() {
          try {
            const r = await originalFetch(`${HOT_ORIGIN}/__hotreload/status`, { cache: 'no-store' });
            if (!r.ok) return false;
            const st = await r.json();
            currentRev = st.rev || currentRev;
            sessionStorage.setItem('ygo_hot_rev', String(currentRev));
            liveFiles = new Set((st.files || []).map((f) => String(f).toLowerCase()));
            updateBadge(`LIVE · Rev #${currentRev}`, '#22c55e');
            return true;
          } catch {
            updateBadge('HOT-RELOAD OFFLINE', '#94a3b8');
            return false;
          }
        }

        // Intercept fetch so the desktop WebView2 app serves newly composed MOD assets directly from RAM
        const originalFetch = window.fetch.bind(window);
        window.fetch = async function (input, init) {
          try {
            const rawUrl = typeof input === 'string' ? input : (input && input.url) || '';
            if (rawUrl && !rawUrl.startsWith(HOT_ORIGIN)) {
              const u = new URL(rawUrl, window.location.href);
              const rel = u.pathname.replace(/^\/+/, '').toLowerCase();
              if (liveFiles.has(rel) || rel.startsWith('mods/') || rel === 'mod-registry.json') {
                const liveUrl = `${HOT_ORIGIN}/__hotreload/files/${u.pathname.replace(/^\/+/, '')}?__hotrev=${currentRev}`;
                const liveInit = Object.assign({}, init || {}, { cache: 'no-store' });
                const resp = await originalFetch(liveUrl, liveInit);
                if (resp.ok) return resp;
              }
            }
          } catch {
            // Fall back to default fetch if hot-reload server is offline
          }
          return originalFetch(input, init);
        };

        let badgeEl = null;
        function ensureBadge() {
          if (badgeEl || !document.body) return badgeEl;
          badgeEl = document.createElement('div');
          badgeEl.id = 'ygo-hot-reload-badge';
          badgeEl.style.cssText = 'position:fixed;top:10px;right:10px;z-index:999998;display:flex;align-items:center;gap:6px;background:rgba(15,23,42,0.92);color:#e2e8f0;border:1px solid #334155;border-radius:999px;padding:4px 10px;font:600 11px/1.2 system-ui,sans-serif;box-shadow:0 4px 16px rgba(0,0,0,0.45);';
          badgeEl.innerHTML = `<span id="ygo-hr-dot" style="width:8px;height:8px;border-radius:50%;background:#22c55e;display:inline-block;"></span><span id="ygo-hr-txt">HOT-RELOAD</span><button id="ygo-hr-auto" style="background:#1e293b;color:#cbd5e1;border:1px solid #475569;border-radius:999px;padding:1px 6px;font-size:10px;cursor:pointer;">${autoReload ? 'AUTO' : 'MANUAL'}</button><button id="ygo-hr-now" style="background:#2563eb;color:#fff;border:none;border-radius:999px;padding:1px 7px;font-size:10px;cursor:pointer;">↻</button>`;
          document.body.appendChild(badgeEl);
          badgeEl.querySelector('#ygo-hr-auto').onclick = () => {
            autoReload = !autoReload;
            sessionStorage.setItem('ygo_hot_auto', autoReload ? '1' : '0');
            badgeEl.querySelector('#ygo-hr-auto').textContent = autoReload ? 'AUTO' : 'MANUAL';
          };
          badgeEl.querySelector('#ygo-hr-now').onclick = () => flushAndReload();
          return badgeEl;
        }

        function updateBadge(text, color) {
          ensureBadge();
          if (!badgeEl) return;
          const dot = badgeEl.querySelector('#ygo-hr-dot');
          const txt = badgeEl.querySelector('#ygo-hr-txt');
          if (dot) dot.style.background = color;
          if (txt) txt.textContent = text;
        }

        function hotSwapCss(rev) {
          document.querySelectorAll('link[rel="stylesheet"]').forEach((lnk) => {
            const href = lnk.getAttribute('href') || '';
            if (href.includes('mods/')) {
              const clean = href.split('?')[0].replace(/^\/+/, '');
              lnk.href = `${HOT_ORIGIN}/__hotreload/files/${clean}?__hotrev=${rev}`;
            }
          });
        }

        async function flushAndReload() {
          try {
            if ('caches' in window) {
              const keys = await caches.keys();
              await Promise.all(keys.map((k) => caches.delete(k)));
            }
          } catch {}
          window.location.reload();
        }

        function connectSse() {
          try {
            const es = new EventSource(`${HOT_ORIGIN}/__hotreload/events`);
            es.addEventListener('hello', (ev) => {
              try {
                const st = JSON.parse(ev.data);
                currentRev = st.rev || currentRev;
                liveFiles = new Set((st.files || []).map((f) => String(f).toLowerCase()));
                updateBadge(`LIVE · Rev #${currentRev}`, '#22c55e');
              } catch {}
            });
            es.addEventListener('reload', async (ev) => {
              try {
                const info = JSON.parse(ev.data);
                currentRev = info.rev || (currentRev + 1);
                sessionStorage.setItem('ygo_hot_rev', String(currentRev));
                await syncLiveFileList();
                if (info.cssOnly) {
                  hotSwapCss(currentRev);
                  updateBadge(`CSS LIVE · Rev #${currentRev}`, '#38bdf8');
                  window.YgoModAPI?.showToast?.(`CSS 热更新完成 (Rev #${currentRev})`, 2000);
                } else if (autoReload) {
                  updateBadge(`RELOADING #${currentRev}...`, '#facc15');
                  window.YgoModAPI?.showToast?.(`检测到 MOD 变动，正在热重载 Rev #${currentRev}...`, 1500);
                  setTimeout(() => flushAndReload(), 220);
                } else {
                  updateBadge(`NEW Rev #${currentRev} (点击 ↻)`, '#facc15');
                }
              } catch {}
            });
            es.addEventListener('error', (ev) => {
              if (ev.data) {
                try {
                  const err = JSON.parse(ev.data);
                  updateBadge('MOD ERROR', '#ef4444');
                  window.YgoModAPI?.showToast?.(`热加载出错: ${err.error}`, 4500);
                } catch {}
              }
            });
          } catch {}
        }

        window.YgoHotReload = {
          origin: HOT_ORIGIN,
          reloadNow: flushAndReload,
          getRev: () => currentRev
        };

        (async () => {
          await syncLiveFileList();
          if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', () => { ensureBadge(); connectSse(); }, { once: true });
          } else {
            ensureBadge();
            connectSse();
          }
        })();
        """;

    private static byte[]? TryReadVanillaZipEntry(string zipPath, string relPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.GetEntry(relPath)
                        ?? zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, relPath, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return null;
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static string GetMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "application/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" or ".webmanifest" => "application/json; charset=utf-8",
        ".wasm" => "application/wasm",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".mp3" => "audio/mpeg",
        ".ogg" => "audio/ogg",
        ".wav" => "audio/wav",
        ".zip" => "application/zip",
        _ => "application/octet-stream"
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private sealed class SseClient(HttpListenerResponse response) : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        public bool IsClosed { get; private set; }

        public async Task SendAsync(string eventName, string data, CancellationToken ct)
        {
            var payload = $"event: {eventName}\ndata: {data.Replace("\r", "").Replace("\n", "\ndata: ")}\n\n";
            await WriteRawAsync(payload, ct);
        }

        public Task SendCommentAsync(string comment, CancellationToken ct) =>
            WriteRawAsync($": {comment}\n\n", ct);

        private async Task WriteRawAsync(string text, CancellationToken ct)
        {
            if (IsClosed) return;
            await _writeLock.WaitAsync(ct);
            try
            {
                if (IsClosed) return;
                var bytes = Encoding.UTF8.GetBytes(text);
                await response.OutputStream.WriteAsync(bytes, ct);
                await response.OutputStream.FlushAsync(ct);
            }
            catch
            {
                IsClosed = true;
                throw;
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose()
        {
            if (IsClosed) return;
            IsClosed = true;
            try { response.Close(); } catch { }
            _writeLock.Dispose();
        }
    }

    private sealed class SilentLogInteraction(Action<LogLevel, string>? log) : IInstallerInteraction
    {
        public void Log(LogLevel level, string message)
        {
            if (level is LogLevel.Warn or LogLevel.Error) log?.Invoke(level, message);
        }

        public void ReportProgress(string status, double? percent = null) { }
        public Task<bool> ConfirmAsync(string prompt, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string?> PromptForPicsDirAsync(string prompt, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
}
