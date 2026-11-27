using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YGORogueDuelistCardModInstaller.Core;
using YGORogueDuelistCardModInstaller.Core.Mcp;
using YGORogueDuelistCardModInstaller.Core.ModManager;

namespace YGORogueDuelistCardModInstaller.Desktop;

/// <summary>
/// Native AOT-safe local loopback HTTP + JSON REST + SSE API server + built-in MCP Server + embedded React SPA host.
/// Serves the Photino WebView2 desktop UI, external AI MCP clients (Streamable HTTP &amp; Legacy SSE), and automation tools.
/// </summary>
public sealed partial class DesktopApiServer : IDisposable
{
    private readonly WorkspaceRegistry _workspaceRegistry = new();
    private readonly Func<string, string?, string?> _pickFolderOnUi;
    private readonly Func<string, (string Name, string[] Extensions)[], string?> _pickOpenFileOnUi;
    private readonly Func<string, string, string?, string?> _pickSaveZipOnUi;

    private readonly object _engineLock = new();
    private readonly ConcurrentDictionary<Guid, SseClient> _sseClients = new();
    private readonly ConcurrentDictionary<string, SseClient> _mcpSseClients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<McpCallRecord> _mcpCallHistory = new();
    private readonly Dictionary<string, string> _embeddedResources = new(StringComparer.OrdinalIgnoreCase);

    private ModInjectionEngine _engine;
    private ModHotReloadServer? _hotReloadServer;
    private VanillaCatalogSnapshot? _vanillaCache;
    private string? _vanillaCacheZipPath;
    private ModStudioSnapshot? _studioCache;
    private string? _studioCacheKey;
    private StudioFocusContext _studioFocus = new();
    private CancellationTokenSource? _activeDownloadCts;
    private string? _activeDownloadLabel;
    private string? _lastProgressStatus;
    private double? _lastProgressPercent;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _isBusy;
    private bool _disposed;

    public DesktopApiServer(
        Func<string, string?, string?> pickFolderOnUi,
        Func<string, (string Name, string[] Extensions)[], string?> pickOpenFileOnUi,
        Func<string, string, string?, string?> pickSaveZipOnUi)
    {
        _pickFolderOnUi = pickFolderOnUi;
        _pickOpenFileOnUi = pickOpenFileOnUi;
        _pickSaveZipOnUi = pickSaveZipOnUi;

        var defaultPath = ModRepository.ResolveWorkspaceRoot();
        var active = _workspaceRegistry.GetActiveWorkspace(defaultPath);
        _engine = new ModInjectionEngine(active?.Path ?? defaultPath);

        IndexEmbeddedWwwRoot();
    }

    public int Port { get; private set; }
    public string BaseUrl => $"http://127.0.0.1:{Port}/";

    public void Start(int preferredPort = 17850)
    {
        HttpListener? listener = null;
        var chosenPort = preferredPort;
        for (var i = 0; i < 30; i++)
        {
            var candidatePort = preferredPort + i;
            var candidate = new HttpListener();
            candidate.Prefixes.Add($"http://127.0.0.1:{candidatePort}/");
            try
            {
                candidate.Start();
                listener = candidate;
                chosenPort = candidatePort;
                break;
            }
            catch
            {
                candidate.Close();
            }
        }

        if (listener is null)
            throw new InvalidOperationException($"Failed to bind local API port {preferredPort}-{preferredPort + 29}.");

        Port = chosenPort;
        _listener = listener;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
    }

    private void IndexEmbeddedWwwRoot()
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (var resName in asm.GetManifestResourceNames())
        {
            var normalized = resName.Replace('\\', '/');
            if (normalized.StartsWith("wwwroot/", StringComparison.OrdinalIgnoreCase))
            {
                var rel = normalized["wwwroot/".Length..];
                _embeddedResources[rel] = resName;
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().WaitAsync(ct); }
            catch { break; }

            _ = Task.Run(() => HandleRequestAsync(ctx, ct), ct);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        res.Headers["Access-Control-Allow-Origin"] = "*";
        res.Headers["Access-Control-Allow-Methods"] = "GET, POST, DELETE, OPTIONS";
        res.Headers["Access-Control-Allow-Headers"] = "Content-Type, Accept, Mcp-Session-Id, Last-Event-ID, MCP-Protocol-Version";
        res.Headers["Access-Control-Expose-Headers"] = "Mcp-Session-Id";

        if (string.Equals(req.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            res.StatusCode = 204;
            res.Close();
            return;
        }

        var path = Uri.UnescapeDataString(req.Url?.AbsolutePath ?? "/");
        try
        {
            // Built-in MCP Server endpoints (Streamable HTTP /mcp + Legacy SSE /mcp/sse & /mcp/messages)
            if (path.Equals("/mcp", StringComparison.OrdinalIgnoreCase))
            {
                await HandleMcpStreamableHttpAsync(req, res, ct);
                return;
            }

            if (path.Equals("/mcp/sse", StringComparison.OrdinalIgnoreCase))
            {
                await HandleMcpLegacySseAsync(res, ct);
                return;
            }

            if (path.Equals("/mcp/messages", StringComparison.OrdinalIgnoreCase))
            {
                await HandleMcpLegacyMessagesAsync(req, res, ct);
                return;
            }

            if (path.Equals("/api/events", StringComparison.OrdinalIgnoreCase))
            {
                await HandleSseAsync(res, ct);
                return;
            }

            if (path.Equals("/api/vanilla/image", StringComparison.OrdinalIgnoreCase))
            {
                await HandleVanillaImageAsync(req, res, ct);
                return;
            }

            if (path.Equals("/api/studio/image", StringComparison.OrdinalIgnoreCase))
            {
                await HandleStudioImageAsync(req, res, ct);
                return;
            }

            if (path.Equals("/api/mods/cover", StringComparison.OrdinalIgnoreCase))
            {
                await HandleModCoverAsync(req, res, ct);
                return;
            }

            if (path.Equals("/api/mods/import-upload", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                var uploadRes = await HandleImportUploadAsync(req, ct);
                await WriteJsonAsync(res, 200, uploadRes, ct);
                return;
            }

            if (path.Equals("/api/mods/cover-upload", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                var coverRes = await HandleCoverUploadAsync(req, ct);
                await WriteJsonAsync(res, 200, coverRes, ct);
                return;
            }

            if (path.Equals("/api/studio/ypk/upload", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                var ypkRes = await HandleStudioUploadYpkAsync(req, ct);
                await WriteJsonAsync(res, 200, ypkRes, ct);
                return;
            }

            if (path.Equals("/api/studio/art/upload", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                var artRes = await HandleStudioUploadArtAsync(req, ct);
                await WriteJsonAsync(res, 200, artRes, ct);
                return;
            }

            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                var body = string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase)
                    ? await ReadJsonBodyAsync(req, ct)
                    : new JsonObject();
                var resultNode = await DispatchApiAsync(req.HttpMethod, path, req, body, ct);
                await WriteJsonAsync(res, 200, resultNode, ct);
                return;
            }

            await ServeStaticFrontendAsync(path, res, ct);
        }
        catch (Exception ex)
        {
            var err = new JsonObject
            {
                ["ok"] = false,
                ["error"] = ex.Message
            };
            try { await WriteJsonAsync(res, 400, err, ct); } catch { }
        }
    }

    private async Task<JsonNode> DispatchApiAsync(string method, string path, HttpListenerRequest req, JsonObject body, CancellationToken ct)
    {
        var route = path.ToLowerInvariant();
        return route switch
        {
            "/api/health" => new JsonObject
            {
                ["ok"] = true,
                ["version"] = ModManifest.CurrentManagerVersion,
                ["port"] = Port,
                ["hotReloadRunning"] = _hotReloadServer is { IsRunning: true },
                ["hotReloadUrl"] = _hotReloadServer is { IsRunning: true } ? _hotReloadServer.BaseUrl : null
            },
            "/api/docs" => BuildApiDocsPayload(Query(req, "lang", "zh")),
            "/api/status" => BuildStatusPayload(),
            "/api/workspaces" => BuildWorkspacesPayload(),
            "/api/workspaces/create" => HandleCreateWorkspace(body),
            "/api/workspaces/switch" => HandleSwitchWorkspace(body),
            "/api/workspaces/remove" => HandleRemoveWorkspace(body),
            "/api/settings" => HandleSaveSettings(body),
            "/api/mods/create" => HandleCreateMod(body),
            "/api/mods/create-example" => HandleCreateExampleMod(body),
            "/api/mods/metadata" => HandleUpdateModMetadata(body),
            "/api/mods/import" => HandleImportMod(body),
            "/api/mods/toggle" => HandleToggleMod(body),
            "/api/mods/reorder" => HandleReorderMods(body),
            "/api/mods/delete" => HandleDeleteMod(body),
            "/api/mods/pack" => HandlePackMod(body),
            "/api/workspace/pack" => HandlePackWorkspace(body),
            "/api/apply" => await HandleApplyAsync(body, ct),
            "/api/restore" => await HandleRestoreAsync(body, ct),
            "/api/update-game" => await HandleUpdateGameAsync(ct),
            "/api/launch" => await HandleLaunchGameAsync(ct),
            "/api/hotreload/status" => BuildHotReloadStatusPayload(),
            "/api/hotreload/start" => await HandleHotReloadStartAsync(body, ct),
            "/api/hotreload/stop" => HandleHotReloadStop(),
            "/api/hotreload/reload" => await HandleHotReloadManualAsync(ct),
            "/api/hotreload/inject" => await HandleHotReloadInjectAsync(ct),
            "/api/studio/presets" => BuildStudioPresetsPayload(),
            "/api/studio/catalog" => HandleStudioCatalog(req),
            "/api/studio/suggest-card-id" => HandleSuggestCardId(req),
            "/api/studio/card/save" => await HandleStudioSaveCardAsync(body),
            "/api/studio/character/save" => await HandleStudioSaveCharacterAsync(body),
            "/api/studio/relic/save" => await HandleStudioSaveRelicAsync(body),
            "/api/studio/pack/save" => await HandleStudioSavePackAsync(body),
            "/api/studio/deck/save" => await HandleStudioSaveDeckAsync(body),
            "/api/studio/ydk/import" => await HandleStudioImportYdkAsync(body),
            "/api/studio/ypk/import" => await HandleStudioImportYpkAsync(body),
            "/api/studio/trainer/save" => await HandleStudioSaveTrainerAsync(body),
            "/api/studio/campaign" => HandleStudioGetCampaign(req),
            "/api/studio/campaign/save" => await HandleStudioSaveCampaignAsync(body),
            "/api/studio/dsl/scaffold" => await HandleStudioScaffoldDslAsync(body),
            "/api/studio/item/delete" => await HandleStudioDeleteItemAsync(body),
            "/api/studio/file" when method.Equals("GET", StringComparison.OrdinalIgnoreCase) => HandleStudioReadFile(req),
            "/api/studio/file" => await HandleStudioWriteFileAsync(body),
            "/api/studio/validate" => HandleStudioValidate(req),
            "/api/studio/context" => HandleUpdateStudioContext(body),
            "/api/mcp/info" => BuildMcpInfoPayload(),
            "/api/mcp/call" => await HandleMcpUiCallAsync(body, ct),
            "/api/mcp/install-config" => HandleMcpInstallConfig(body),
            "/api/mcp/clear-logs" => HandleMcpClearLogs(),
            "/api/updates/check" => await HandleUpdatesCheckAsync(req, ct),
            "/api/updates/test-proxy" => await HandleTestProxyAsync(body, ct),
            "/api/updates/download-game" => await HandleDownloadGameUpdateAsync(body, ct),
            "/api/updates/download-manager" => await HandleDownloadManagerUpdateAsync(body, ct),
            "/api/updates/cancel" => HandleCancelDownload(),
            "/api/export/linux-bundle" => await HandleExportLinuxBundleAsync(body, ct),
            "/api/export/android-bundle" => await HandleExportAndroidBundleAsync(body, ct),
            "/api/vanilla/dll-info" => HandleVanillaDllInfo(),
            "/api/vanilla/catalog" => HandleVanillaCatalog(req),
            "/api/vanilla/detail" => HandleVanillaDetail(req),
            "/api/vanilla/clone" => await HandleVanillaCloneAsync(body),
            "/api/dialog/pick-folder" => HandlePickFolder(body),
            "/api/dialog/pick-file" => HandlePickFile(body),
            "/api/dialog/save-zip" => HandleSaveZipDialog(body),
            "/api/shell/open-folder" => HandleOpenFolder(body),
            "/api/shell/open-url" => HandleOpenUrl(body),
            _ => throw new ModOperationException($"Unknown API endpoint: {path}")
        };
    }

    // ==================== STATUS & WORKSPACES ====================

    private JsonObject BuildStatusPayload()
    {
        var engine = GetEngine();
        var state = engine.Repository.LoadState();
        var status = engine.GetStatus();

        var modsArr = new JsonArray();
        foreach (var m in status.Mods)
        {
            var coverPath = ModRepository.ResolveCoverPath(m.DirectoryPath, m.Manifest);
            var coverUrl = coverPath is not null
                ? $"/api/mods/cover?modId={Uri.EscapeDataString(m.Id)}&v={File.GetLastWriteTimeUtc(coverPath).Ticks}"
                : null;
            var resolvedJs = ModRepository.ResolveJsEntries(m.DirectoryPath, m.Manifest);
            var resolvedCss = ModRepository.ResolveCssEntries(m.DirectoryPath, m.Manifest);
            var resolvedLuaLibs = ModRepository.ResolveLuaLibs(m.DirectoryPath, m.Manifest);
            var settingsCount = ModRepository.ResolveSettingsSchema(m.DirectoryPath, m.Manifest)?.Count ?? 0;

            modsArr.Add((JsonNode?)new JsonObject
            {
                ["id"] = m.Id,
                ["name"] = m.Manifest.Name,
                ["version"] = m.Manifest.Version,
                ["author"] = m.Manifest.Author,
                ["description"] = m.Manifest.Description,
                ["category"] = m.Manifest.Category,
                ["effectiveCategory"] = ModRepository.ResolveEffectiveCategory(m.Manifest, m.Stats),
                ["cover"] = m.Manifest.Cover,
                ["coverUrl"] = coverUrl,
                ["autoDiscoverEntries"] = m.Manifest.AutoDiscoverEntries,
                ["resolvedJsEntries"] = ToStrArray(resolvedJs),
                ["resolvedCssEntries"] = ToStrArray(resolvedCss),
                ["resolvedLuaLibs"] = ToStrArray(resolvedLuaLibs),
                ["settingsCount"] = settingsCount,
                ["gameVersion"] = m.Manifest.GameVersion,
                ["minGameVersion"] = m.Manifest.MinGameVersion,
                ["maxGameVersion"] = m.Manifest.MaxGameVersion,
                ["minManagerVersion"] = m.Manifest.MinManagerVersion,
                ["directoryPath"] = m.DirectoryPath,
                ["enabled"] = m.Enabled,
                ["order"] = m.Order,
                ["tags"] = ToStrArray(m.Manifest.Tags),
                ["stats"] = new JsonObject
                {
                    ["hasLegacyModJson"] = m.Stats.HasLegacyModJson,
                    ["hasLuaDsl"] = m.Stats.HasLuaDsl,
                    ["hasJsDsl"] = m.Stats.HasJsDsl,
                    ["cardCount"] = m.Stats.CardCount,
                    ["characterCount"] = m.Stats.CharacterCount,
                    ["relicCount"] = m.Stats.RelicCount,
                    ["packCount"] = m.Stats.PackCount,
                    ["preconstructedDeckCount"] = m.Stats.PreconstructedDeckCount,
                    ["scriptCount"] = m.Stats.ScriptCount,
                    ["luaLibCount"] = m.Stats.LuaLibCount,
                    ["jsPluginCount"] = m.Stats.JsPluginCount,
                    ["cssStyleCount"] = m.Stats.CssStyleCount,
                    ["deckCount"] = m.Stats.DeckCount,
                    ["artCount"] = m.Stats.ArtCount,
                    ["campaignFileCount"] = m.Stats.CampaignFileCount,
                    ["overrideFileCount"] = m.Stats.OverrideFileCount,
                    ["hasTrainer"] = m.Stats.HasTrainer,
                    ["overridePaths"] = ToStrArray(m.Stats.OverrideRelativePaths)
                }
            });
        }

        var conflictsArr = new JsonArray();
        foreach (var c in status.Conflicts)
        {
            conflictsArr.Add((JsonNode?)new JsonObject
            {
                ["category"] = c.Category,
                ["resourceKey"] = c.ResourceKey,
                ["modIds"] = ToStrArray(c.ModIds),
                ["winnerModId"] = c.WinnerModId
            });
        }

        var issuesArr = new JsonArray();
        foreach (var v in status.VersionIssues)
        {
            issuesArr.Add((JsonNode?)new JsonObject
            {
                ["modId"] = v.ModId,
                ["modName"] = v.ModName,
                ["message"] = v.Message
            });
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["apiBaseUrl"] = BaseUrl,
            ["managerVersion"] = ModManifest.CurrentManagerVersion,
            ["workspaceRoot"] = status.WorkspaceRoot,
            ["modsRoot"] = status.ModsRoot,
            ["installDir"] = status.InstallDir ?? string.Empty,
            ["gameBaseDir"] = status.GameBaseDir ?? string.Empty,
            ["hasGameExe"] = !string.IsNullOrWhiteSpace(status.InstallDir) && File.Exists(Path.Combine(status.InstallDir, GameLocator.GameExeName)),
            ["hasWebBundle"] = !string.IsNullOrWhiteSpace(status.ActiveBaseGameVersion),
            ["gameInstalled"] = !string.IsNullOrWhiteSpace(status.InstallDir) || !string.IsNullOrWhiteSpace(status.ActiveBaseGameVersion),
            ["picsDir"] = state.PicsDir ?? GameLocator.FindPicsDir() ?? string.Empty,
            ["repo"] = string.IsNullOrWhiteSpace(state.Repo) ? InstallOptions.DefaultRepo : state.Repo,
            ["githubProxy"] = GitHubReleaseService.NormalizeProxyPrefix(state.GitHubProxy),
            ["managerRepo"] = string.IsNullOrWhiteSpace(state.ManagerRepo) ? InstallOptions.DefaultManagerRepo : state.ManagerRepo,
            ["mcpUrl"] = $"{BaseUrl.TrimEnd('/')}/mcp",
            ["mcpSseUrl"] = $"{BaseUrl.TrimEnd('/')}/mcp/sse",
            ["mcpToolCount"] = McpToolDispatcher.BuildToolDefinitions().Count,
            ["isDownloading"] = _activeDownloadCts is not null,
            ["activeDownloadLabel"] = _activeDownloadLabel,
            ["downloadProgress"] = new JsonObject
            {
                ["active"] = _activeDownloadCts is not null,
                ["label"] = _activeDownloadLabel,
                ["status"] = _lastProgressStatus,
                ["percent"] = _lastProgressPercent
            },
            ["activeBaseGameVersion"] = status.ActiveBaseGameVersion,
            ["launcherBuild"] = status.LauncherMeta?.LauncherBuild ?? 0,
            ["mode"] = status.Mode.ToString(),
            ["developerMode"] = status.DeveloperMode,
            ["language"] = string.IsNullOrWhiteSpace(state.Language) ? "zh" : state.Language,
            ["syncState"] = status.SyncState.ToString(),
            ["syncSummary"] = status.SyncSummary,
            ["isBusy"] = _isBusy,
            ["hotReload"] = BuildHotReloadStatusPayload(),
            ["mods"] = modsArr,
            ["conflicts"] = conflictsArr,
            ["versionIssues"] = issuesArr,
            ["installedVirtualVersions"] = ToStrArray(status.InstalledVirtualVersions)
        };
    }

    private JsonObject BuildWorkspacesPayload()
    {
        var current = GetEngine().Repository.WorkspaceRoot;
        var list = _workspaceRegistry.ListWorkspaces(current);
        var active = _workspaceRegistry.GetActiveWorkspace(current);
        var arr = new JsonArray();
        foreach (var w in list)
        {
            arr.Add((JsonNode?)new JsonObject
            {
                ["id"] = w.Id,
                ["name"] = w.Name,
                ["path"] = w.Path,
                ["description"] = w.Description,
                ["isActive"] = string.Equals(w.Id, active?.Id, StringComparison.OrdinalIgnoreCase)
            });
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["activeId"] = active?.Id,
            ["workspaces"] = arr
        };
    }

    private JsonObject HandleCreateWorkspace(JsonObject body)
    {
        var path = Str(body, "path");
        var name = Str(body, "name");
        var desc = Str(body, "description");
        if (string.IsNullOrWhiteSpace(path)) throw new ModOperationException("Workspace path is required.");
        if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileName(path);

        var profile = _workspaceRegistry.CreateWorkspace(name, path, desc, setActive: true);
        SwitchEngineWorkspace(profile.Path);
        EmitLog(LogLevel.Ok, $"Created & switched workspace: {profile.Name} -> {profile.Path}");
        return new JsonObject { ["ok"] = true, ["workspaceId"] = profile.Id, ["path"] = profile.Path };
    }

    private JsonObject HandleSwitchWorkspace(JsonObject body)
    {
        var target = Str(body, "idOrPath");
        if (string.IsNullOrWhiteSpace(target)) throw new ModOperationException("Target workspace id or path is required.");
        WorkspaceProfile profile = Directory.Exists(target)
            ? _workspaceRegistry.EnsureRegistered(target, setActive: true)
            : _workspaceRegistry.SetActiveWorkspace(target);
        SwitchEngineWorkspace(profile.Path);
        EmitLog(LogLevel.Ok, $"Switched workspace: {profile.Name} -> {profile.Path}");
        return new JsonObject { ["ok"] = true, ["workspaceId"] = profile.Id, ["path"] = profile.Path };
    }

    private JsonObject HandleRemoveWorkspace(JsonObject body)
    {
        var id = Str(body, "id");
        _workspaceRegistry.RemoveWorkspace(id);
        var next = _workspaceRegistry.GetActiveWorkspace();
        if (next is not null) SwitchEngineWorkspace(next.Path);
        return new JsonObject { ["ok"] = true };
    }

    private JsonObject HandleSaveSettings(JsonObject body)
    {
        var engine = GetEngine();
        var state = engine.Repository.LoadState();
        if (body.ContainsKey("installDir")) state.InstallDir = NullIfBlank(Str(body, "installDir"));
        if (body.ContainsKey("gameDir")) state.GameDir = NullIfBlank(Str(body, "gameDir"));
        if (body.ContainsKey("picsDir")) state.PicsDir = NullIfBlank(Str(body, "picsDir"));
        if (body.ContainsKey("repo"))
        {
            var r = Str(body, "repo");
            state.Repo = string.IsNullOrWhiteSpace(r) ? InstallOptions.DefaultRepo : r;
        }
        if (body.ContainsKey("githubProxy"))
        {
            state.GitHubProxy = GitHubReleaseService.NormalizeProxyPrefix(Str(body, "githubProxy"));
        }
        if (body.ContainsKey("managerRepo"))
        {
            var mr = Str(body, "managerRepo");
            state.ManagerRepo = string.IsNullOrWhiteSpace(mr) ? InstallOptions.DefaultManagerRepo : mr;
        }
        if (body.ContainsKey("mode"))
        {
            state.InjectionMode = Str(body, "mode").Equals("LauncherManifest", StringComparison.OrdinalIgnoreCase)
                ? InjectionMode.LauncherManifest
                : InjectionMode.VirtualBundle;
        }
        if (body["developerMode"] is JsonValue dv && dv.TryGetValue<bool>(out var dev)) state.DeveloperMode = dev;
        if (body.ContainsKey("language")) state.Language = Str(body, "language", "zh");

        engine.Repository.SaveState(state);
        InvalidateCaches();
        return new JsonObject { ["ok"] = true };
    }

    // ==================== MOD LIBRARY ====================

    private JsonObject HandleCreateMod(JsonObject body)
    {
        var engine = GetEngine();
        var status = engine.GetStatus();
        var complete = Bool(body, "completeExample", false);
        var created = complete
            ? engine.Repository.CreateCompleteExampleMod(
                NullIfBlank(Str(body, "id")),
                status.ActiveBaseGameVersion)
            : engine.Repository.CreateTemplateMod(
                Str(body, "id"),
                NullIfBlank(Str(body, "name")),
                NullIfBlank(Str(body, "author")),
                NullIfBlank(Str(body, "description")),
                status.ActiveBaseGameVersion,
                NullIfBlank(Str(body, "minGameVersion")),
                NullIfBlank(Str(body, "category")),
                NullIfBlank(Str(body, "coverSourcePath")));
        InvalidateStudioCache();
        EmitLog(LogLevel.Ok, $"Created MOD: {created.Manifest.Name} ({created.Id})");
        TriggerHotReloadBackground($"create-mod:{created.Id}");
        return new JsonObject { ["ok"] = true, ["modId"] = created.Id, ["directory"] = created.DirectoryPath };
    }

    private JsonObject HandleCreateExampleMod(JsonObject body)
    {
        var engine = GetEngine();
        var status = engine.GetStatus();
        var created = engine.Repository.CreateCompleteExampleMod(
            NullIfBlank(Str(body, "id")),
            status.ActiveBaseGameVersion);
        InvalidateStudioCache();
        EmitLog(LogLevel.Ok, $"Created complete showcase example MOD: {created.Manifest.Name} ({created.Id})");
        TriggerHotReloadBackground($"create-example:{created.Id}");
        return new JsonObject { ["ok"] = true, ["modId"] = created.Id, ["name"] = created.Manifest.Name, ["directory"] = created.DirectoryPath };
    }

    private JsonObject HandleUpdateModMetadata(JsonObject body)
    {
        var modId = Str(body, "modId");
        if (string.IsNullOrWhiteSpace(modId)) throw new ModOperationException("MOD ID is required.");

        List<string>? tags = null;
        if (body["tags"] is JsonArray tArr)
        {
            tags = tArr.Select(x => x?.GetValue<string>() ?? string.Empty).Where(s => s.Length > 0).ToList();
        }
        else if (body.ContainsKey("tagsCsv"))
        {
            tags = Str(body, "tagsCsv")
                .Split([',', '，', ';'], StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
        }

        bool? autoEntries = body["autoDiscoverEntries"] is JsonValue av && av.TryGetValue<bool>(out var ab) ? ab : null;

        var updated = GetEngine().Repository.UpdateModMetadata(
            modId,
            name: body.ContainsKey("name") ? Str(body, "name") : null,
            version: body.ContainsKey("version") ? Str(body, "version") : null,
            author: body.ContainsKey("author") ? Str(body, "author") : null,
            description: body.ContainsKey("description") ? Str(body, "description") : null,
            category: body.ContainsKey("category") ? Str(body, "category") : null,
            minGameVersion: body.ContainsKey("minGameVersion") ? Str(body, "minGameVersion") : null,
            maxGameVersion: body.ContainsKey("maxGameVersion") ? Str(body, "maxGameVersion") : null,
            tags: tags,
            autoDiscoverEntries: autoEntries,
            coverSourcePath: body.ContainsKey("coverSourcePath") ? NullIfBlank(Str(body, "coverSourcePath")) : null,
            clearCover: Bool(body, "clearCover", false));

        InvalidateStudioCache();
        EmitLog(LogLevel.Ok, $"Updated MOD metadata: {updated.Manifest.Name} ({updated.Id})");
        TriggerHotReloadBackground($"metadata:{updated.Id}");
        return new JsonObject
        {
            ["ok"] = true,
            ["modId"] = updated.Id,
            ["category"] = ModRepository.ResolveEffectiveCategory(updated.Manifest, updated.Stats),
            ["cover"] = updated.Manifest.Cover
        };
    }

    private async Task HandleModCoverAsync(HttpListenerRequest req, HttpListenerResponse res, CancellationToken ct)
    {
        var modId = Query(req, "modId");
        var mod = GetEngine().Repository.LoadMods()
            .FirstOrDefault(m => string.Equals(m.Id, modId, StringComparison.OrdinalIgnoreCase));
        var coverPath = mod is not null ? ModRepository.ResolveCoverPath(mod.DirectoryPath, mod.Manifest) : null;
        if (coverPath is null || !File.Exists(coverPath))
        {
            res.StatusCode = 404;
            res.Close();
            return;
        }

        var bytes = await File.ReadAllBytesAsync(coverPath, ct);
        res.ContentType = GuessMime(coverPath);
        res.Headers["Cache-Control"] = "public, max-age=60";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes, ct);
        res.Close();
    }

    private async Task<JsonObject> HandleImportUploadAsync(HttpListenerRequest req, CancellationToken ct)
    {
        var fileName = Path.GetFileName(Query(req, "fileName", "upload.yrdpk"));
        var isArchive = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                        fileName.EndsWith(".yrdpk", StringComparison.OrdinalIgnoreCase);
        var isYpk = fileName.EndsWith(".ypk", StringComparison.OrdinalIgnoreCase);
        if (!isArchive && !isYpk)
        {
            throw new ModOperationException("仅支持拖拽导入 .yrdpk / .zip / .ypk 格式的 MOD、YGOPro 扩展包或工作区整合包。");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"ygo-drop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tempArchive = Path.Combine(tempDir, fileName);
        try
        {
            await using (var fs = File.Create(tempArchive))
            {
                await req.InputStream.CopyToAsync(fs, ct);
            }

            var engine = GetEngine();
            if (YpkImporter.IsYpkArchive(tempArchive))
            {
                var ypk = YpkImporter.ImportYpk(engine.Repository, tempArchive, overwrite: true);
                InvalidateStudioCache();
                EmitLog(LogLevel.Ok, $"Dropped & imported .ypk: {ypk.Mod.Manifest.Name} ({ypk.CardCount} cards, {ypk.ScriptCount} Lua, {ypk.ArtCount} art)");
                TriggerHotReloadBackground($"drop-import-ypk:{ypk.Mod.Id}");
                return new JsonObject
                {
                    ["ok"] = true,
                    ["isWorkspacePack"] = false,
                    ["isYpk"] = true,
                    ["modId"] = ypk.Mod.Id,
                    ["name"] = ypk.Mod.Manifest.Name,
                    ["cardCount"] = ypk.CardCount,
                    ["scriptCount"] = ypk.ScriptCount,
                    ["artCount"] = ypk.ArtCount
                };
            }

            if (ModPackager.IsWorkspacePackZip(tempArchive))
            {
                var (manifest, count) = ModPackager.ImportWorkspacePack(engine.Repository, tempArchive, overwrite: true);
                InvalidateStudioCache();
                EmitLog(LogLevel.Ok, $"Dropped & imported workspace modpack: {manifest.Name} v{manifest.Version} ({count} MODs)");
                TriggerHotReloadBackground("drop-import-pack");
                return new JsonObject { ["ok"] = true, ["isWorkspacePack"] = true, ["name"] = manifest.Name, ["count"] = count };
            }

            var imported = engine.Repository.ImportMod(tempArchive, overwrite: true);
            InvalidateStudioCache();
            EmitLog(LogLevel.Ok, $"Dropped & imported MOD: {imported.Manifest.Name} ({imported.Id})");
            TriggerHotReloadBackground($"drop-import:{imported.Id}");
            return new JsonObject { ["ok"] = true, ["isWorkspacePack"] = false, ["modId"] = imported.Id, ["name"] = imported.Manifest.Name };
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    private async Task<JsonObject> HandleCoverUploadAsync(HttpListenerRequest req, CancellationToken ct)
    {
        var modId = Query(req, "modId");
        var fileName = Path.GetFileName(Query(req, "fileName", "cover.png"));
        if (string.IsNullOrWhiteSpace(modId)) throw new ModOperationException("MOD ID is required.");

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is not (".png" or ".webp" or ".jpg" or ".jpeg" or ".svg" or ".gif"))
        {
            throw new ModOperationException($"不支持的封面格式：{ext}（请使用 .png / .webp / .jpg / .svg）");
        }

        var tempImg = Path.Combine(Path.GetTempPath(), $"ygo-cover-{Guid.NewGuid():N}{ext}");
        try
        {
            await using (var fs = File.Create(tempImg))
            {
                await req.InputStream.CopyToAsync(fs, ct);
            }

            var updated = GetEngine().Repository.UpdateModMetadata(modId, coverSourcePath: tempImg);
            InvalidateStudioCache();
            EmitLog(LogLevel.Ok, $"Updated cover for MOD '{updated.Id}' -> {updated.Manifest.Cover}");
            return new JsonObject { ["ok"] = true, ["modId"] = updated.Id, ["cover"] = updated.Manifest.Cover };
        }
        finally
        {
            if (File.Exists(tempImg))
            {
                try { File.Delete(tempImg); } catch { }
            }
        }
    }

    private JsonObject HandleImportMod(JsonObject body)
    {
        var sourcePath = Str(body, "sourcePath");
        var force = Bool(body, "force", false);
        var engine = GetEngine();

        if (YpkImporter.IsYpkArchive(sourcePath))
        {
            var ypk = YpkImporter.ImportYpk(engine.Repository, sourcePath, overwrite: force);
            InvalidateStudioCache();
            EmitLog(LogLevel.Ok, $"Imported YGOPro/MDPro3 .ypk: {ypk.Mod.Manifest.Name} ({ypk.CardCount} cards, {ypk.ScriptCount} Lua, {ypk.ArtCount} art)");
            TriggerHotReloadBackground($"import-ypk:{ypk.Mod.Id}");
            return new JsonObject
            {
                ["ok"] = true,
                ["isWorkspacePack"] = false,
                ["isYpk"] = true,
                ["modId"] = ypk.Mod.Id,
                ["name"] = ypk.Mod.Manifest.Name,
                ["cardCount"] = ypk.CardCount,
                ["scriptCount"] = ypk.ScriptCount,
                ["artCount"] = ypk.ArtCount
            };
        }

        if (ModPackager.IsWorkspacePackZip(sourcePath))
        {
            var (manifest, count) = ModPackager.ImportWorkspacePack(engine.Repository, sourcePath, overwrite: true);
            InvalidateStudioCache();
            EmitLog(LogLevel.Ok, $"Imported workspace modpack: {manifest.Name} v{manifest.Version} ({count} MODs)");
            return new JsonObject { ["ok"] = true, ["isWorkspacePack"] = true, ["name"] = manifest.Name, ["count"] = count };
        }

        var imported = engine.Repository.ImportMod(sourcePath, overwrite: force);
        InvalidateStudioCache();
        EmitLog(LogLevel.Ok, $"Imported MOD: {imported.Manifest.Name} ({imported.Id})");
        return new JsonObject { ["ok"] = true, ["isWorkspacePack"] = false, ["modId"] = imported.Id, ["name"] = imported.Manifest.Name };
    }

    private JsonObject HandleToggleMod(JsonObject body)
    {
        var engine = GetEngine();
        var state = engine.Repository.LoadState();
        var mods = engine.Repository.LoadMods(state);
        var modId = Str(body, "modId");
        var enabled = Bool(body, "enabled", true);

        if (modId.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var m in mods) m.Enabled = enabled;
        }
        else
        {
            var target = mods.FirstOrDefault(m => string.Equals(m.Id, modId, StringComparison.OrdinalIgnoreCase))
                ?? throw new ModOperationException($"MOD not found: {modId}");
            target.Enabled = enabled;
        }

        engine.Repository.SaveModOrderAndFlags(mods, state);
        TriggerHotReloadBackground("toggle");
        return new JsonObject { ["ok"] = true };
    }

    private JsonObject HandleReorderMods(JsonObject body)
    {
        var engine = GetEngine();
        var state = engine.Repository.LoadState();
        var mods = engine.Repository.LoadMods(state);
        var orderedIds = (body["orderedIds"] as JsonArray ?? [])
            .Select(n => n?.GetValue<string>() ?? string.Empty)
            .Where(s => s.Length > 0)
            .ToList();

        var byId = mods.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        var reordered = new List<ManagedMod>();
        foreach (var id in orderedIds)
        {
            if (byId.Remove(id, out var m)) reordered.Add(m);
        }
        reordered.AddRange(byId.Values);
        for (var i = 0; i < reordered.Count; i++) reordered[i].Order = i;

        engine.Repository.SaveModOrderAndFlags(reordered, state);
        TriggerHotReloadBackground("reorder");
        return new JsonObject { ["ok"] = true };
    }

    private JsonObject HandleDeleteMod(JsonObject body)
    {
        var modId = Str(body, "modId");
        GetEngine().Repository.DeleteMod(modId);
        InvalidateStudioCache();
        EmitLog(LogLevel.Ok, $"Deleted MOD: {modId}");
        TriggerHotReloadBackground("delete-mod");
        return new JsonObject { ["ok"] = true };
    }

    private JsonObject HandlePackMod(JsonObject body)
    {
        var modId = Str(body, "modId");
        var outputPath = NullIfBlank(Str(body, "outputPath"));
        var zipPath = ModPackager.PackSingleMod(GetEngine().Repository, modId, outputPath);
        EmitLog(LogLevel.Ok, $"Packed MOD '{modId}' -> {zipPath}");
        return new JsonObject { ["ok"] = true, ["zipPath"] = zipPath };
    }

    private JsonObject HandlePackWorkspace(JsonObject body)
    {
        var (zipPath, count, minVer) = ModPackager.PackWorkspace(
            GetEngine().Repository,
            NullIfBlank(Str(body, "outputPath")),
            NullIfBlank(Str(body, "name")),
            NullIfBlank(Str(body, "version")),
            NullIfBlank(Str(body, "description")),
            NullIfBlank(Str(body, "minGameVersion")),
            Bool(body, "enabledOnly", false));
        EmitLog(LogLevel.Ok, $"Packed workspace ({count} MODs) -> {zipPath}");
        return new JsonObject { ["ok"] = true, ["zipPath"] = zipPath, ["modCount"] = count, ["minGameVersion"] = minVer };
    }

    // ==================== APPLY / RESTORE / UPDATE / LAUNCH ====================

    private async Task<JsonObject> HandleApplyAsync(JsonObject body, CancellationToken ct)
    {
        if (_isBusy) throw new ModOperationException("Another operation is currently running.");
        _isBusy = true;
        var interaction = new SseInstallerInteraction(EmitLog, EmitProgress);
        try
        {
            var engine = GetEngine();
            var modeStr = Str(body, "mode");
            InjectionMode? modeOverride = modeStr.Equals("LauncherManifest", StringComparison.OrdinalIgnoreCase)
                ? InjectionMode.LauncherManifest
                : modeStr.Equals("VirtualBundle", StringComparison.OrdinalIgnoreCase)
                    ? InjectionMode.VirtualBundle
                    : null;

            int? hrPort = _hotReloadServer is { IsRunning: true } ? _hotReloadServer.Port : null;
            if (Bool(body, "injectHotReload", false))
            {
                if (_hotReloadServer is not { IsRunning: true })
                {
                    await StartHotReloadInternalAsync(ModHotReloadServer.DefaultPort, ct);
                }
                hrPort = _hotReloadServer?.Port;
            }

            var launchAfter = Bool(body, "launchAfter", false);
            var statusBefore = engine.GetStatus();
            var hasExe = !string.IsNullOrWhiteSpace(statusBefore.InstallDir) &&
                         File.Exists(Path.Combine(statusBefore.InstallDir, GameLocator.GameExeName));

            await engine.ApplyAsync(new ApplyModsOptions
            {
                ModeOverride = modeOverride,
                BuildOnlyDir = NullIfBlank(Str(body, "buildOnlyDir")),
                LaunchGameAfter = launchAfter && hasExe,
                DeveloperModeOverride = body["developerMode"] is JsonValue dv && dv.TryGetValue<bool>(out var dev) ? dev : null,
                IgnoreVersionRequirements = Bool(body, "ignoreVersion", false),
                AssumeYes = true,
                ForceKillRunningGame = true,
                HotReloadPort = hrPort
            }, interaction, ct);

            string? launchedWebUrl = null;
            if (launchAfter && !hasExe && string.IsNullOrWhiteSpace(Str(body, "buildOnlyDir")))
            {
                if (_hotReloadServer is not { IsRunning: true })
                {
                    await StartHotReloadInternalAsync(ModHotReloadServer.DefaultPort, ct);
                }
                launchedWebUrl = _hotReloadServer?.BaseUrl;
                if (!string.IsNullOrWhiteSpace(launchedWebUrl))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo { FileName = launchedWebUrl, UseShellExecute = true });
                    }
                    catch { /* ignore on headless hosts */ }
                    EmitLog(LogLevel.Ok, $"未检测到原版 EXE，已自动以独立 web-.zip 模式启动并打开浏览器：{launchedWebUrl}");
                }
            }

            EmitProgress("Completed", 100);
            return new JsonObject { ["ok"] = true, ["launchedWebUrl"] = launchedWebUrl };
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async Task<JsonObject> HandleRestoreAsync(JsonObject body, CancellationToken ct)
    {
        if (_isBusy) throw new ModOperationException("Another operation is currently running.");
        _isBusy = true;
        var interaction = new SseInstallerInteraction(EmitLog, EmitProgress);
        try
        {
            await GetEngine().RestoreVanillaAsync(interaction, forceKillRunningGame: true, cancellationToken: ct);
            EmitProgress("Vanilla restored", 100);
            return new JsonObject { ["ok"] = true };
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async Task<JsonObject> HandleUpdateGameAsync(CancellationToken ct)
    {
        if (_isBusy) throw new ModOperationException("Another operation is currently running.");
        _isBusy = true;
        var interaction = new SseInstallerInteraction(EmitLog, EmitProgress);
        try
        {
            await GetEngine().UpdateOfficialGameAsync(interaction, ct);
            InvalidateCaches();
            EmitProgress("Update check complete", 100);
            return new JsonObject { ["ok"] = true };
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async Task<JsonObject> HandleLaunchGameAsync(CancellationToken ct)
    {
        var status = GetEngine().GetStatus();
        var hasExe = !string.IsNullOrWhiteSpace(status.InstallDir) &&
                     File.Exists(Path.Combine(status.InstallDir, GameLocator.GameExeName));
        if (hasExe)
        {
            ModInjectionEngine.LaunchGame(status.InstallDir);
            EmitLog(LogLevel.Ok, "Launched Yu-Gi-Oh! Rogue Duelist.");
            return new JsonObject { ["ok"] = true, ["mode"] = "exe" };
        }

        if (string.IsNullOrWhiteSpace(status.ActiveBaseGameVersion))
        {
            throw new ModOperationException("未检测到原版 EXE 或独立 web-*.zip 数据包，请先点击「下载 web-.zip 立即开玩」。");
        }

        if (_hotReloadServer is not { IsRunning: true })
        {
            await StartHotReloadInternalAsync(ModHotReloadServer.DefaultPort, ct);
        }
        var url = _hotReloadServer?.BaseUrl ?? "http://127.0.0.1:17845/";
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
        EmitLog(LogLevel.Ok, $"独立 web-.zip 模式：已在默认浏览器打开 {url}");
        return new JsonObject { ["ok"] = true, ["mode"] = "web", ["url"] = url };
    }

    // ==================== HOT-RELOAD ====================

    private JsonObject BuildHotReloadStatusPayload() => new()
    {
        ["running"] = _hotReloadServer is { IsRunning: true },
        ["port"] = _hotReloadServer?.Port ?? 0,
        ["url"] = _hotReloadServer is { IsRunning: true } ? _hotReloadServer.BaseUrl : null,
        ["lanBound"] = _hotReloadServer is { IsRunning: true, LanBound: true },
        ["lanUrls"] = ToStrArray(_hotReloadServer is { IsRunning: true } ? _hotReloadServer.LanUrls : []),
        ["revision"] = _hotReloadServer?.Revision ?? 0,
        ["lastError"] = _hotReloadServer?.LastError
    };

    private async Task StartHotReloadInternalAsync(int preferredPort, CancellationToken ct)
    {
        if (_hotReloadServer is { IsRunning: true }) return;
        _hotReloadServer = new ModHotReloadServer(GetEngine(), EmitLog);
        _hotReloadServer.Reloaded += info =>
        {
            BroadcastSse("hotreload", new JsonObject
            {
                ["running"] = true,
                ["port"] = _hotReloadServer?.Port ?? 0,
                ["url"] = _hotReloadServer?.BaseUrl,
                ["revision"] = info.Revision,
                ["cssOnly"] = info.CssOnly,
                ["error"] = info.Error,
                ["changedFiles"] = ToStrArray(info.ChangedFiles)
            });
        };
        await _hotReloadServer.StartAsync(preferredPort, ct);
    }

    private async Task<JsonObject> HandleHotReloadStartAsync(JsonObject body, CancellationToken ct)
    {
        var port = IntVal(body, "port", ModHotReloadServer.DefaultPort);
        await StartHotReloadInternalAsync(port, ct);
        return new JsonObject { ["ok"] = true, ["hotReload"] = BuildHotReloadStatusPayload() };
    }

    private JsonObject HandleHotReloadStop()
    {
        StopHotReloadInternal();
        return new JsonObject { ["ok"] = true, ["hotReload"] = BuildHotReloadStatusPayload() };
    }

    private async Task<JsonObject> HandleHotReloadManualAsync(CancellationToken ct)
    {
        if (_hotReloadServer is not { IsRunning: true }) throw new ModOperationException("Hot-reload server is not running.");
        var info = await _hotReloadServer.TriggerManualReloadAsync("(manual)", ct);
        return new JsonObject { ["ok"] = true, ["revision"] = info.Revision, ["error"] = info.Error };
    }

    private async Task<JsonObject> HandleHotReloadInjectAsync(CancellationToken ct)
    {
        if (_hotReloadServer is not { IsRunning: true })
        {
            await StartHotReloadInternalAsync(ModHotReloadServer.DefaultPort, ct);
        }

        return await HandleApplyAsync(new JsonObject
        {
            ["mode"] = "VirtualBundle",
            ["developerMode"] = true,
            ["injectHotReload"] = true
        }, ct);
    }

    private void StopHotReloadInternal()
    {
        if (_hotReloadServer is not null)
        {
            _hotReloadServer.Dispose();
            _hotReloadServer = null;
        }
    }

    private void TriggerHotReloadBackground(string reason)
    {
        if (_hotReloadServer is { IsRunning: true } srv)
        {
            _ = srv.TriggerManualReloadAsync(reason);
        }
    }

    // ==================== VISUAL MOD STUDIO API ====================

    private JsonObject BuildStudioPresetsPayload()
    {
        var presets = new JsonArray();
        foreach (var p in ModStudioService.CardTypePresets)
        {
            presets.Add((JsonNode?)new JsonObject
            {
                ["label"] = p.Label,
                ["displayType"] = p.DisplayType,
                ["typeMask"] = p.TypeMask,
                ["isMonster"] = p.IsMonster,
                ["defaultRace"] = p.DefaultRace
            });
        }

        var races = new JsonObject();
        foreach (var (k, v) in ModStudioService.MonsterRaceMasks) races[k] = v;

        var attrs = new JsonObject();
        foreach (var (k, v) in ModStudioService.AttributeMasks)
        {
            if (!string.IsNullOrWhiteSpace(k)) attrs[k] = v;
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["cardPresets"] = presets,
            ["monsterRaces"] = races,
            ["attributes"] = attrs,
            ["luaCardTemplates"] = new JsonArray(
                MakeOpt("draw1", "Draw 1 Card / 抽 1 张卡", ModStudioService.BuildLuaCardTemplate("draw1", 190010001, "Custom Card")),
                MakeOpt("draw2", "Draw 2 Cards / 抽 2 张卡", ModStudioService.BuildLuaCardTemplate("draw2", 190010001, "Custom Card")),
                MakeOpt("gain_lp", "Gain 1000 LP / 回复 1000 基本分", ModStudioService.BuildLuaCardTemplate("gain_lp", 190010001, "Custom Card")),
                MakeOpt("destroy_monster", "Destroy 1 Opp Monster / 破坏对方 1 只怪兽", ModStudioService.BuildLuaCardTemplate("destroy_monster", 190010001, "Custom Card")),
                MakeOpt("monster_summon_draw", "On Summon Draw 1 / 召唤抽 1", ModStudioService.BuildLuaCardTemplate("monster_summon_draw", 190010001, "Custom Card")),
                MakeOpt("synchro_monster", "Synchro Summon (5D's) / 同调怪兽模板", ModStudioService.BuildLuaCardTemplate("synchro_monster", 190010001, "Custom Synchro")),
                MakeOpt("xyz_monster", "Xyz Summon (ZEXAL) / 超量怪兽模板", ModStudioService.BuildLuaCardTemplate("xyz_monster", 190010001, "Custom Xyz")),
                MakeOpt("pendulum_monster", "Pendulum Scale (ARC-V) / 灵摆怪兽模板", ModStudioService.BuildLuaCardTemplate("pendulum_monster", 190010001, "Custom Pendulum")),
                MakeOpt("link_monster", "Link Summon (VRAINS) / 连接怪兽模板", ModStudioService.BuildLuaCardTemplate("link_monster", 190010001, "Custom Link")),
                MakeOpt("blank", "Blank Skeleton / 空白骨架", ModStudioService.BuildLuaCardTemplate("blank", 190010001, "Custom Card"))),
            ["luaRelicTemplates"] = new JsonArray(
                MakeOpt("gain_lp", "Gain 1000 LP / 回复 1000 LP", ModStudioService.BuildLuaRelicTemplate("gain_lp")),
                MakeOpt("draw_extra", "Extra Draw 1 / 额外抽 1", ModStudioService.BuildLuaRelicTemplate("draw_extra")),
                MakeOpt("damage_opp", "Burn 800 / 对方 800 伤害", ModStudioService.BuildLuaRelicTemplate("damage_opp")),
                MakeOpt("blank", "Custom Lua / 自定义 Lua", ModStudioService.BuildLuaRelicTemplate("blank")))
        };

        static JsonObject MakeOpt(string key, string label, string code) => new()
        {
            ["key"] = key, ["label"] = label, ["code"] = code
        };
    }

    private ModStudioSnapshot GetOrLoadStudioSnapshot(string modId)
    {
        var mod = ResolveManagedMod(modId);
        var vanillaZip = TryGetVanillaZipPath();
        var cacheKey = $"{mod.DirectoryPath}|{vanillaZip}";
        if (_studioCache is not null && string.Equals(_studioCacheKey, cacheKey, StringComparison.OrdinalIgnoreCase))
        {
            return _studioCache;
        }

        _studioCache = ModStudioCatalog.Load(mod.Id, mod.DirectoryPath, vanillaZip);
        _studioCacheKey = cacheKey;
        return _studioCache;
    }

    private JsonObject HandleStudioCatalog(HttpListenerRequest req)
    {
        var modId = Query(req, "modId");
        var cat = Query(req, "category", "cards").ToLowerInvariant();
        var q = Query(req, "q").Trim();
        var offset = Math.Max(0, QueryInt(req, "offset", 0));
        var limit = Math.Clamp(QueryInt(req, "limit", 60), 1, 200);
        var refresh = Query(req, "refresh") == "1";
        if (refresh) InvalidateStudioCache();

        var snap = GetOrLoadStudioSnapshot(modId);
        var vanilla = TryGetVanillaCatalog();

        var campaignCfgExists = File.Exists(Path.Combine(snap.ModDir, "campaign-config.json")) ||
                                File.Exists(Path.Combine(snap.ModDir, "removals.json"));
        var campaignPyCount = Directory.Exists(Path.Combine(snap.ModDir, "campaign"))
            ? Directory.GetFiles(Path.Combine(snap.ModDir, "campaign"), "*", SearchOption.AllDirectories).Length
            : 0;

        var counts = new JsonObject
        {
            ["cards"] = snap.Cards.Count,
            ["characters"] = snap.Characters.Count,
            ["relics"] = snap.Relics.Count,
            ["packs"] = snap.Packs.Count,
            ["decks"] = snap.Decks.Count,
            ["trainer"] = snap.Trainer.Exists ? 1 : 0,
            ["campaign"] = (campaignCfgExists ? 1 : 0) + campaignPyCount,
            ["code"] = snap.TextFiles.Count
        };

        var itemsArr = new JsonArray();
        var total = 0;

        if (cat == "cards")
        {
            var matched = snap.Cards.Where(c => Matches(q, c.Name, c.ZhName, c.Id.ToString(CultureInfo.InvariantCulture), c.DisplayType, c.SourceTag)).ToList();
            total = matched.Count;
            foreach (var c in matched.Skip(offset).Take(limit))
            {
                itemsArr.Add((JsonNode?)new JsonObject
                {
                    ["key"] = c.Id.ToString(CultureInfo.InvariantCulture),
                    ["id"] = c.Id,
                    ["name"] = c.Name,
                    ["zhName"] = c.ZhName,
                    ["desc"] = c.Desc,
                    ["zhDesc"] = c.ZhDesc,
                    ["displayType"] = c.DisplayType,
                    ["race"] = c.Race,
                    ["attribute"] = c.Attribute,
                    ["level"] = c.Level,
                    ["atk"] = c.Atk,
                    ["def"] = c.Def,
                    ["typeMask"] = c.TypeMask,
                    ["raceMask"] = c.RaceMask,
                    ["attributeMask"] = c.AttributeMask,
                    ["packId"] = c.PackId,
                    ["luaScript"] = c.LuaScript,
                    ["sourceTag"] = c.SourceTag,
                    ["sourceFilePath"] = c.SourceFilePath,
                    ["artLocalPath"] = c.Image.LocalFilePath ?? string.Empty,
                    ["imageUrl"] = BuildStudioImageUrl(snap, c.Image)
                });
            }
        }
        else if (cat == "characters")
        {
            var matched = snap.Characters.Where(ch => Matches(q, ch.Name, ch.ZhName, ch.Sprite, ch.StartingRelic, ch.SourceTag)).ToList();
            total = matched.Count;
            foreach (var ch in matched.Skip(offset).Take(limit))
            {
                itemsArr.Add((JsonNode?)new JsonObject
                {
                    ["key"] = ch.Key,
                    ["index"] = ch.Index,
                    ["name"] = ch.Name,
                    ["zhName"] = ch.ZhName,
                    ["sprite"] = ch.Sprite,
                    ["race"] = ch.Race,
                    ["attribute"] = ch.Attribute,
                    ["color"] = ch.Color,
                    ["startingRelic"] = ch.StartingRelic,
                    ["startingPacks"] = ToStrArray(ch.StartingPacks),
                    ["deckCards"] = ToStrArray(ch.DeckCardNames),
                    ["unlockedByDefault"] = ch.UnlockedByDefault,
                    ["sourceTag"] = ch.SourceTag,
                    ["sourceFilePath"] = ch.SourceFilePath,
                    ["imageUrl"] = BuildStudioImageUrl(snap, ch.Image),
                    ["subCards"] = BuildSubCardsByNames(snap, vanilla, ch.DeckCardNames)
                });
            }
        }
        else if (cat == "relics")
        {
            var matched = snap.Relics.Where(r => Matches(q, r.Key, r.Name, r.ZhName, r.Tag, r.Description)).ToList();
            total = matched.Count;
            foreach (var r in matched.Skip(offset).Take(limit))
            {
                itemsArr.Add((JsonNode?)new JsonObject
                {
                    ["key"] = r.Key,
                    ["name"] = r.Name,
                    ["zhName"] = r.ZhName,
                    ["cost"] = r.Cost,
                    ["tag"] = r.Tag,
                    ["description"] = r.Description,
                    ["zhDescription"] = r.ZhDescription,
                    ["art"] = r.Image.LocalFilePath ?? r.Art,
                    ["luaEffect"] = r.LuaEffect,
                    ["sourceTag"] = r.SourceTag,
                    ["sourceFilePath"] = r.SourceFilePath,
                    ["imageUrl"] = BuildStudioImageUrl(snap, r.Image, relicKeyForDataUri: r.Key)
                });
            }
        }
        else if (cat == "packs")
        {
            var matched = snap.Packs.Where(p => Matches(q, p.Id, p.Name, p.ZhName, p.Code)).ToList();
            total = matched.Count;
            foreach (var p in matched.Skip(offset).Take(limit))
            {
                var allIds = p.UltraIds.Concat(p.SuperIds).Concat(p.RareIds).Concat(p.CommonIds).Distinct().ToList();
                itemsArr.Add((JsonNode?)new JsonObject
                {
                    ["key"] = p.Id,
                    ["id"] = p.Id,
                    ["name"] = p.Name,
                    ["zhName"] = p.ZhName,
                    ["code"] = p.Code,
                    ["size"] = p.Size,
                    ["commonIds"] = ToIntArray(p.CommonIds),
                    ["rareIds"] = ToIntArray(p.RareIds),
                    ["superIds"] = ToIntArray(p.SuperIds),
                    ["ultraIds"] = ToIntArray(p.UltraIds),
                    ["totalCards"] = p.TotalCardCount,
                    ["sourceTag"] = p.SourceTag,
                    ["sourceFilePath"] = p.SourceFilePath,
                    ["artLocalPath"] = p.Image.LocalFilePath ?? string.Empty,
                    ["imageUrl"] = BuildStudioImageUrl(snap, p.Image),
                    ["subCards"] = BuildSubCardsByIds(snap, vanilla, allIds)
                });
            }
        }
        else if (cat == "decks")
        {
            var matched = snap.Decks.Where(d => Matches(q, d.Id, d.Name, d.ZhName, d.Art)).ToList();
            total = matched.Count;
            foreach (var d in matched.Skip(offset).Take(limit))
            {
                itemsArr.Add((JsonNode?)new JsonObject
                {
                    ["key"] = d.Id,
                    ["id"] = d.Id,
                    ["name"] = d.Name,
                    ["zhName"] = d.ZhName,
                    ["art"] = d.Art,
                    ["artLocalPath"] = d.Image.LocalFilePath ?? string.Empty,
                    ["cardIds"] = ToIntArray(d.CardIds),
                    ["isYdk"] = d.IsYdk,
                    ["sourceTag"] = d.SourceTag,
                    ["sourceFilePath"] = d.SourceFilePath,
                    ["imageUrl"] = BuildStudioImageUrl(snap, d.Image),
                    ["subCards"] = BuildSubCardsByIds(snap, vanilla, d.CardIds)
                });
            }
        }
        else if (cat == "code")
        {
            var matched = snap.TextFiles.Where(f => Matches(q, f)).ToList();
            total = matched.Count;
            foreach (var f in matched.Skip(offset).Take(limit))
            {
                itemsArr.Add((JsonNode?)new JsonObject
                {
                    ["key"] = f,
                    ["path"] = f,
                    ["ext"] = Path.GetExtension(f).TrimStart('.').ToUpperInvariant()
                });
            }
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["modId"] = snap.ModId,
            ["category"] = cat,
            ["counts"] = counts,
            ["total"] = total,
            ["offset"] = offset,
            ["hasMore"] = offset + itemsArr.Count < total,
            ["items"] = itemsArr,
            ["trainer"] = new JsonObject
            {
                ["exists"] = snap.Trainer.Exists,
                ["unlockAllCharacters"] = snap.Trainer.UnlockAllCharacters,
                ["unlockAllVariants"] = snap.Trainer.UnlockAllVariants,
                ["startingGold"] = snap.Trainer.StartingGold,
                ["startingGoldBonus"] = snap.Trainer.StartingGoldBonus,
                ["startingLp"] = snap.Trainer.StartingLp,
                ["freeShop"] = snap.Trainer.FreeShop,
                ["shopPriceMultiplier"] = snap.Trainer.ShopPriceMultiplier,
                ["rewardGoldMultiplier"] = snap.Trainer.RewardGoldMultiplier,
                ["startingRelics"] = ToStrArray(snap.Trainer.StartingRelics),
                ["inGameOverlay"] = snap.Trainer.InGameOverlay
            }
        };
    }

    private JsonObject HandleSuggestCardId(HttpListenerRequest req)
    {
        var mod = ResolveManagedMod(Query(req, "modId"));
        return new JsonObject { ["ok"] = true, ["nextId"] = ModStudioService.SuggestNextCardId(mod.DirectoryPath) };
    }

    private async Task<JsonObject> HandleStudioSaveCardAsync(JsonObject body)
    {
        var mod = ResolveManagedMod(Str(body, "modId"));
        var id = IntVal(body, "id", 0);
        var typeMask = IntVal(body, "typeMask", 2);
        var preset = ModStudioService.CardTypePresets.FirstOrDefault(p => p.TypeMask == typeMask)
                     ?? ModStudioService.CardTypePresets[4];

        var saved = ModStudioService.SaveVisualCard(
            mod.DirectoryPath, id, Str(body, "name"), NullIfBlank(Str(body, "zhName")),
            Str(body, "desc"), NullIfBlank(Str(body, "zhDesc")), preset,
            Str(body, "race", preset.DefaultRace), Str(body, "attribute"),
            IntVal(body, "level"), IntVal(body, "atk"), IntVal(body, "def"),
            NullIfBlank(Str(body, "packId")), NullIfBlank(Str(body, "artPath")),
            NullIfBlank(Str(body, "luaScript")));

        InvalidateStudioCache();
        EmitLog(LogLevel.Ok, $"Saved Card #{id} -> {saved}");
        await TriggerHotReloadIfRunningAsync("cards/custom-cards.json");
        return new JsonObject { ["ok"] = true, ["path"] = saved };
    }

    private async Task<JsonObject> HandleStudioSaveCharacterAsync(JsonObject body)
    {
        var mod = ResolveManagedMod(Str(body, "modId"));
        var saved = ModStudioService.SaveVisualCharacter(
            mod.DirectoryPath, Str(body, "name"), NullIfBlank(Str(body, "zhName")),
            Str(body, "sprite", "dm01.png"), Str(body, "race", "Spellcaster"),
            Str(body, "attribute", "DARK"), Str(body, "color", "#38bdf8"),
            Str(body, "startingRelic", "mage"), Str(body, "startingPacks"),
            Str(body, "deckCards"), Bool(body, "unlockedByDefault", true));

        InvalidateStudioCache();
        EmitLog(LogLevel.Ok, $"Saved Character -> {saved}");
        await TriggerHotReloadIfRunningAsync(Path.GetFileName(saved));
        return new JsonObject { ["ok"] = true, ["path"] = saved };
    }

    private async Task<JsonObject> HandleStudioSaveRelicAsync(JsonObject body)
    {
        var mod = ResolveManagedMod(Str(body, "modId"));
        var saved = ModStudioService.SaveVisualRelic(
            mod.DirectoryPath, Str(body, "key"), Str(body, "name"), NullIfBlank(Str(body, "zhName")),
            IntVal(body, "cost", 80), Str(body, "tag", "utility"),
            Str(body, "description"), NullIfBlank(Str(body, "zhDescription")),
            NullIfBlank(Str(body, "art")), NullIfBlank(Str(body, "luaEffect")));

        InvalidateStudioCache();
        EmitLog(LogLevel.Ok, $"Saved Relic -> {saved}");
        await TriggerHotReloadIfRunningAsync(Path.GetFileName(saved));
        return new JsonObject { ["ok"] = true, ["path"] = saved };
    }

    private async Task<JsonObject> HandleStudioSavePackAsync(JsonObject body)
    {
        var mod = ResolveManagedMod(Str(body, "modId"));
        var saved = ModStudioService.SaveVisualPack(
            mod.DirectoryPath, Str(body, "id"), Str(body, "name"), NullIfBlank(Str(body, "zhName")),
            Str(body, "code"), IntVal(body, "size", 9),
            Str(body, "commonIds"), Str(body, "rareIds"), Str(body, "superIds"), Str(body, "ultraIds"),
            NullIfBlank(Str(body, "coverPath")));

        InvalidateStudioCache();
        EmitLog(LogLevel.Ok, $"Saved Booster Pack -> {saved}");
        await TriggerHotReloadIfRunningAsync(Path.GetFileName(saved));
        return new JsonObject { ["ok"] = true, ["path"] = saved };
    }

    private async Task<JsonObject> HandleStudioSaveDeckAsync(JsonObject body)
    {
        var mod = ResolveManagedMod(Str(body, "modId"));
        var saved = ModStudioService.SaveVisualPreconstructedDeck(
            mod.DirectoryPath, Str(body, "id"), Str(body, "name"), NullIfBlank(Str(body, "zhName")),
            Str(body, "art", "SDK"), Str(body, "cardIds"),
            NullIfBlank(Str(body, "coverPath")));

        InvalidateStudioCache();
        EmitLog(LogLevel.Ok, $"Saved Preconstructed Deck -> {saved}");
        await TriggerHotReloadIfRunningAsync(Path.GetFileName(saved));
        return new JsonObject { ["ok"] = true, ["path"] = saved };
    }

    private async Task<JsonObject> HandleStudioUploadArtAsync(HttpListenerRequest req, CancellationToken ct)
    {
        var modId = Query(req, "modId");
        var kind = Query(req, "kind", "card").ToLowerInvariant();
        var key = Query(req, "key", "custom");
        var fileName = Path.GetFileName(Query(req, "fileName", "art.png"));
        var mod = ResolveManagedMod(modId);

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".svg")) ext = ".png";

        using var ms = new MemoryStream();
        await req.InputStream.CopyToAsync(ms, ct);
        if (ms.Length == 0) throw new ModOperationException("上传的图片内容为空。");
        if (ms.Length > 20 * 1024 * 1024) throw new ModOperationException("上传的图片不能超过 20 MB。");

        var safeKey = kind == "card"
            ? new string(key.Where(char.IsDigit).ToArray())
            : ModRepository.SanitizeId(key);
        if (string.IsNullOrWhiteSpace(safeKey)) safeKey = "custom";

        string targetSubDir = kind switch
        {
            "pack" => Path.Combine("art", "packs"),
            "deck" => Path.Combine("art", "decks"),
            "character" => Path.Combine("art", "characters"),
            _ => "art"
        };
        var outFileName = kind == "relic" ? $"relic_{safeKey.Replace('-', '_')}{ext}" : $"{safeKey}{ext}";
        if (kind == "pack" && !string.IsNullOrWhiteSpace(key))
        {
            // Keep exact pack ID (e.g. YPK-CUSTOM-MOD.jpg) if filename-safe
            var cleanPack = key.Trim();
            if (cleanPack.IndexOfAny(Path.GetInvalidFileNameChars()) < 0) outFileName = $"{cleanPack}{ext}";
        }
        if (kind == "deck" && !string.IsNullOrWhiteSpace(key))
        {
            var cleanDeck = key.Trim();
            if (cleanDeck.IndexOfAny(Path.GetInvalidFileNameChars()) < 0) outFileName = $"{cleanDeck}{ext}";
        }

        var fullDir = Path.Combine(mod.DirectoryPath, targetSubDir);
        Directory.CreateDirectory(fullDir);
        var fullPath = Path.Combine(fullDir, outFileName);
        await File.WriteAllBytesAsync(fullPath, ms.ToArray(), ct);

        var relPath = Path.GetRelativePath(mod.DirectoryPath, fullPath).Replace('\\', '/');
        InvalidateStudioCache();
        EmitLog(LogLevel.Ok, $"Uploaded {kind} artwork -> {relPath}");
        return new JsonObject
        {
            ["ok"] = true,
            ["localPath"] = fullPath,
            ["relPath"] = relPath,
            ["previewUrl"] = $"/api/studio/image?absPath={Uri.EscapeDataString(fullPath)}&v={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
        };
    }

    private async Task<JsonObject> HandleStudioImportYdkAsync(JsonObject body)
    {
        var mod = ResolveManagedMod(Str(body, "modId"));
        var dest = ModStudioService.ImportYdkFile(mod.DirectoryPath, Str(body, "sourcePath"));
        InvalidateStudioCache();
        EmitLog(LogLevel.Ok, $"Imported .ydk -> {dest}");
        await TriggerHotReloadIfRunningAsync(Path.GetFileName(dest));
        return new JsonObject { ["ok"] = true, ["path"] = dest };
    }

    private async Task<JsonObject> HandleStudioImportYpkAsync(JsonObject body)
    {
        var mod = ResolveManagedMod(Str(body, "modId"));
        var sourcePath = Str(body, "sourcePath");
        var res = YpkImporter.ImportYpkIntoExistingMod(mod.DirectoryPath, sourcePath);
        InvalidateStudioCache();
        EmitLog(LogLevel.Ok, $"Merged .ypk into '{mod.Id}': {res.CardCount} cards, {res.ScriptCount} Lua, {res.ArtCount} art, {res.DeckCount} decks");
        await TriggerHotReloadIfRunningAsync($"studio-ypk:{mod.Id}");
        return new JsonObject
        {
            ["ok"] = true,
            ["modId"] = mod.Id,
            ["cardCount"] = res.CardCount,
            ["scriptCount"] = res.ScriptCount,
            ["luaLibCount"] = res.LuaLibCount,
            ["artCount"] = res.ArtCount,
            ["deckCount"] = res.DeckCount,
            ["packId"] = res.PackId
        };
    }

    private async Task<JsonObject> HandleStudioUploadYpkAsync(HttpListenerRequest req, CancellationToken ct)
    {
        var modId = Query(req, "modId");
        var fileName = Path.GetFileName(Query(req, "fileName", "expansion.ypk"));
        var mod = ResolveManagedMod(modId);
        if (!fileName.EndsWith(".ypk", StringComparison.OrdinalIgnoreCase) &&
            !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new ModOperationException("请拖入 .ypk 扩展卡包文件。");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"ygo-studio-ypk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, fileName);
        try
        {
            await using (var fs = File.Create(tempPath))
            {
                await req.InputStream.CopyToAsync(fs, ct);
            }

            var res = YpkImporter.ImportYpkIntoExistingMod(mod.DirectoryPath, tempPath);
            InvalidateStudioCache();
            EmitLog(LogLevel.Ok, $"Dropped & merged .ypk into '{mod.Id}': {res.CardCount} cards, {res.ScriptCount} Lua, {res.ArtCount} art");
            await TriggerHotReloadIfRunningAsync($"studio-ypk:{mod.Id}");
            return new JsonObject
            {
                ["ok"] = true,
                ["modId"] = mod.Id,
                ["cardCount"] = res.CardCount,
                ["scriptCount"] = res.ScriptCount,
                ["luaLibCount"] = res.LuaLibCount,
                ["artCount"] = res.ArtCount,
                ["deckCount"] = res.DeckCount,
                ["packId"] = res.PackId
            };
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    private async Task<JsonObject> HandleStudioSaveTrainerAsync(JsonObject body)
    {
        var mod = ResolveManagedMod(Str(body, "modId"));
        var saved = ModStudioService.SaveVisualTrainer(
            mod.DirectoryPath,
            Bool(body, "unlockAllCharacters", false),
            Bool(body, "unlockAllVariants", false),
            NullableInt(body, "startingGold"),
            NullableInt(body, "startingGoldBonus"),
            NullableInt(body, "startingLp"),
            Bool(body, "freeShop", false),
            NullableDouble(body, "shopPriceMultiplier"),
            NullableDouble(body, "rewardGoldMultiplier"),
            Str(body, "startingRelics"),
            Bool(body, "inGameOverlay", true));

        InvalidateStudioCache();
        EmitLog(LogLevel.Ok, $"Saved trainer.json -> {saved}");
        await TriggerHotReloadIfRunningAsync("trainer.json");
        return new JsonObject { ["ok"] = true, ["path"] = saved };
    }

    private JsonObject HandleStudioGetCampaign(HttpListenerRequest req)
    {
        var mod = ResolveManagedMod(Query(req, "modId"));
        var cfgPath = Path.Combine(mod.DirectoryPath, "campaign-config.json");
        var rmPath = Path.Combine(mod.DirectoryPath, "removals.json");
        JsonObject cfg;
        var exists = false;
        if (File.Exists(cfgPath))
        {
            cfg = ModPatcher.ParseJson(File.ReadAllText(cfgPath, Encoding.UTF8)).AsObject();
            exists = true;
        }
        else if (File.Exists(rmPath))
        {
            cfg = ModPatcher.ParseJson(File.ReadAllText(rmPath, Encoding.UTF8)).AsObject();
            exists = true;
        }
        else
        {
            cfg = new JsonObject();
        }

        var removals = cfg["removals"] as JsonObject ?? cfg;
        var campaignFiles = new JsonArray();
        var campDir = Path.Combine(mod.DirectoryPath, "campaign");
        if (Directory.Exists(campDir))
        {
            foreach (var f in Directory.GetFiles(campDir, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var rel = Path.GetRelativePath(mod.DirectoryPath, f).Replace('\\', '/');
                campaignFiles.Add((JsonNode?)JsonValue.Create(rel));
            }
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["exists"] = exists,
            ["config"] = new JsonObject
            {
                ["duelRules"] = cfg["duelRules"]?.GetValue<string>() ?? mod.Manifest.DuelRules ?? string.Empty,
                ["startingGold"] = cfg["startingGold"]?.DeepClone(),
                ["startingLp"] = cfg["startingLp"]?.DeepClone(),
                ["bossEvery"] = cfg["bossEvery"]?.DeepClone(),
                ["shopPackCount"] = cfg["shopPackCount"]?.DeepClone(),
                ["rerollBasePrice"] = cfg["rerollBasePrice"]?.DeepClone(),
                ["banishPrice"] = cfg["banishPrice"]?.DeepClone(),
                ["disableBossCurses"] = Bool(cfg, "disableBossCurses", false),
                ["disableShadowRelics"] = Bool(cfg, "disableShadowRelics", false),
                ["disableRelicSeal"] = Bool(cfg, "disableRelicSeal", false),
                ["disableRelicBanish"] = Bool(cfg, "disableRelicBanish", false),
                ["disableRelicFusions"] = Bool(cfg, "disableRelicFusions", false),
                ["removeCardIds"] = (removals["removeCardIds"] ?? removals["cards"])?.DeepClone() ?? new JsonArray(),
                ["removeCharacterIndices"] = removals["removeCharacterIndices"]?.DeepClone() ?? new JsonArray(),
                ["removeCharacterNames"] = (removals["removeCharacterNames"] ?? removals["characters"])?.DeepClone() ?? new JsonArray(),
                ["removePackIds"] = (removals["removePackIds"] ?? removals["packs"])?.DeepClone() ?? new JsonArray(),
                ["removeDeckIds"] = (removals["removeDeckIds"] ?? removals["decks"])?.DeepClone() ?? new JsonArray(),
                ["removeRelicKeys"] = (removals["removeRelicKeys"] ?? removals["relics"])?.DeepClone() ?? new JsonArray(),
                ["removeCurseKeys"] = (removals["removeCurseKeys"] ?? removals["curses"])?.DeepClone() ?? new JsonArray()
            },
            ["campaignFiles"] = campaignFiles
        };
    }

    private async Task<JsonObject> HandleStudioSaveCampaignAsync(JsonObject body)
    {
        var mod = ResolveManagedMod(Str(body, "modId"));
        var outObj = new JsonObject();

        var duelRules = Str(body, "duelRules").Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(duelRules)) outObj["duelRules"] = duelRules;
        if (NullableInt(body, "startingGold") is int sg) outObj["startingGold"] = sg;
        if (NullableInt(body, "startingLp") is int slp) outObj["startingLp"] = slp;
        if (NullableInt(body, "bossEvery") is int be) outObj["bossEvery"] = be;
        if (NullableInt(body, "shopPackCount") is int spc) outObj["shopPackCount"] = spc;
        if (NullableInt(body, "rerollBasePrice") is int rbp) outObj["rerollBasePrice"] = rbp;
        if (NullableInt(body, "banishPrice") is int bp) outObj["banishPrice"] = bp;

        outObj["disableBossCurses"] = Bool(body, "disableBossCurses", false);
        outObj["disableShadowRelics"] = Bool(body, "disableShadowRelics", false);
        outObj["disableRelicSeal"] = Bool(body, "disableRelicSeal", false);
        outObj["disableRelicBanish"] = Bool(body, "disableRelicBanish", false);
        outObj["disableRelicFusions"] = Bool(body, "disableRelicFusions", false);

        var removals = new JsonObject
        {
            ["removeCardIds"] = ParseIntJsonArray(body["removeCardIds"]),
            ["removeCharacterIndices"] = ParseIntJsonArray(body["removeCharacterIndices"]),
            ["removeCharacterNames"] = ParseStrJsonArray(body["removeCharacterNames"]),
            ["removePackIds"] = ParseStrJsonArray(body["removePackIds"]),
            ["removeDeckIds"] = ParseStrJsonArray(body["removeDeckIds"]),
            ["removeRelicKeys"] = ParseStrJsonArray(body["removeRelicKeys"]),
            ["removeCurseKeys"] = ParseStrJsonArray(body["removeCurseKeys"])
        };
        outObj["removals"] = removals;

        var cfgPath = Path.Combine(mod.DirectoryPath, "campaign-config.json");
        await File.WriteAllTextAsync(cfgPath, outObj.ToJsonString(ModPatcher.JsonOptions), ModPatcher.Utf8NoBom);

        InvalidateStudioCache();
        EmitLog(LogLevel.Ok, $"Saved campaign-config.json -> {cfgPath}");
        await TriggerHotReloadIfRunningAsync("campaign-config.json");
        return new JsonObject { ["ok"] = true, ["path"] = cfgPath };

        static JsonArray ParseIntJsonArray(JsonNode? node)
        {
            var arr = new JsonArray();
            var seen = new HashSet<int>();
            if (node is JsonArray ja)
            {
                foreach (var n in ja)
                    if (n is not null && int.TryParse(n.ToString().Trim(), out var v) && seen.Add(v)) arr.Add((JsonNode?)JsonValue.Create(v));
            }
            else if (node is JsonValue jv && jv.TryGetValue<string>(out var s))
            {
                foreach (var tok in s.Split([',', '，', ';', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries))
                    if (int.TryParse(tok.Trim(), out var v) && seen.Add(v)) arr.Add((JsonNode?)JsonValue.Create(v));
            }
            return arr;
        }

        static JsonArray ParseStrJsonArray(JsonNode? node)
        {
            var arr = new JsonArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (node is JsonArray ja)
            {
                foreach (var n in ja)
                {
                    var s = n?.ToString().Trim();
                    if (!string.IsNullOrEmpty(s) && seen.Add(s)) arr.Add((JsonNode?)JsonValue.Create(s));
                }
            }
            else if (node is JsonValue jv && jv.TryGetValue<string>(out var s))
            {
                foreach (var tok in s.Split([',', '，', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var t = tok.Trim();
                    if (!string.IsNullOrEmpty(t) && seen.Add(t)) arr.Add((JsonNode?)JsonValue.Create(t));
                }
            }
            return arr;
        }
    }

    private async Task<JsonObject> HandleStudioScaffoldDslAsync(JsonObject body)
    {
        var mod = ResolveManagedMod(Str(body, "modId"));
        var (luaPath, jsPath) = ModStudioService.ScaffoldXmakeDsl(mod);
        InvalidateStudioCache();
        EmitLog(LogLevel.Ok, $"Scaffolded Xmake DSL: {Path.GetFileName(luaPath)}, {Path.GetFileName(jsPath)}");
        await TriggerHotReloadIfRunningAsync("mod.lua");
        return new JsonObject { ["ok"] = true, ["luaPath"] = "mod.lua", ["jsPath"] = "mod.js" };
    }

    private async Task<JsonObject> HandleStudioDeleteItemAsync(JsonObject body)
    {
        var mod = ResolveManagedMod(Str(body, "modId"));
        var cat = Str(body, "category").ToLowerInvariant();
        var ok = cat == "cards" && int.TryParse(Str(body, "key"), out var cid)
            ? ModStudioCatalog.DeleteCardFromModularJson(mod.DirectoryPath, cid)
            : ModStudioCatalog.DeleteModularSourceFile(mod.DirectoryPath, Str(body, "sourceFilePath"));

        if (!ok)
            throw new ModOperationException("该条目定义在 mod.lua 或 legacy data/mod.json 中，请在「代码/DSL」面板直接编辑。");

        InvalidateStudioCache();
        EmitLog(LogLevel.Ok, $"Deleted {cat} item from {mod.Id}");
        await TriggerHotReloadIfRunningAsync("delete");
        return new JsonObject { ["ok"] = true };
    }

    private JsonObject HandleStudioReadFile(HttpListenerRequest req)
    {
        var mod = ResolveManagedMod(Query(req, "modId"));
        var rel = Query(req, "path");
        var content = ModStudioService.ReadModTextFile(mod.DirectoryPath, rel);
        return new JsonObject { ["ok"] = true, ["path"] = rel, ["content"] = content };
    }

    private async Task<JsonObject> HandleStudioWriteFileAsync(JsonObject body)
    {
        var mod = ResolveManagedMod(Str(body, "modId"));
        var rel = Str(body, "path");
        var saved = ModStudioService.WriteModTextFile(mod.DirectoryPath, rel, Str(body, "content"));
        InvalidateStudioCache();
        EmitLog(LogLevel.Ok, $"Saved {rel} -> {saved}");
        await TriggerHotReloadIfRunningAsync(rel);
        return new JsonObject { ["ok"] = true, ["path"] = rel };
    }

    private JsonObject HandleStudioValidate(HttpListenerRequest req)
    {
        var engine = GetEngine();
        var status = engine.GetStatus();
        var report = ModStudioService.ValidateMod(engine.Repository, Query(req, "modId"), status.ActiveBaseGameVersion);
        return new JsonObject
        {
            ["ok"] = true,
            ["isValid"] = report.IsValid,
            ["errors"] = ToStrArray(report.Errors),
            ["warnings"] = ToStrArray(report.Warnings)
        };
    }

    // ==================== VANILLA OFFLINE CATALOG API (INFINITE SCROLL) ====================

    private VanillaCatalogSnapshot? TryGetVanillaCatalog()
    {
        var status = GetEngine().GetStatus();
        var vanilla = ModInjectionEngine.FindBestVanillaSource(status.InstallDir, status.GameBaseDir, status.LauncherMeta);
        if (vanilla is null) return null;

        if (_vanillaCache is not null && string.Equals(_vanillaCacheZipPath, vanilla.Value.ZipPath, StringComparison.OrdinalIgnoreCase))
        {
            return _vanillaCache;
        }

        _vanillaCache = VanillaContentCatalog.Load(vanilla.Value.ZipPath, vanilla.Value.Version);
        _vanillaCacheZipPath = vanilla.Value.ZipPath;
        return _vanillaCache;
    }

    private string? TryGetVanillaZipPath()
    {
        var status = GetEngine().GetStatus();
        return ModInjectionEngine.FindBestVanillaSource(status.InstallDir, status.GameBaseDir, status.LauncherMeta)?.ZipPath;
    }

    private JsonObject HandleVanillaCatalog(HttpListenerRequest req)
    {
        if (Query(req, "refresh") == "1") { _vanillaCache = null; _vanillaCacheZipPath = null; }
        var catSnap = TryGetVanillaCatalog()
            ?? throw new ModOperationException("未检测到本地原版游戏 web-X.Y.Z.zip，请先在「设置」中指定游戏安装目录。");

        var category = Query(req, "category", "cards").ToLowerInvariant();
        var q = Query(req, "q").Trim();
        var offset = Math.Max(0, QueryInt(req, "offset", 0));
        var limit = Math.Clamp(QueryInt(req, "limit", 60), 1, 200);

        var itemsArr = new JsonArray();
        var total = 0;

        switch (category)
        {
            case "cards":
                var mc = catSnap.Cards.Where(c => Matches(q, c.Name, c.ZhName, c.Id.ToString(CultureInfo.InvariantCulture), c.DisplayType, c.Race, c.Attribute)).ToList();
                total = mc.Count;
                foreach (var c in mc.Skip(offset).Take(limit))
                {
                    itemsArr.Add((JsonNode?)new JsonObject
                    {
                        ["key"] = c.Id.ToString(CultureInfo.InvariantCulture),
                        ["title"] = string.IsNullOrWhiteSpace(c.ZhName) ? c.Name : c.ZhName,
                        ["subtitle"] = $"#{c.Id} · {c.Name}",
                        ["badge"] = c.Level > 0 ? $"Lv{c.Level} {c.Atk}/{c.Def}" : c.DisplayType,
                        ["imageUrl"] = BuildVanillaImageUrl(c.ThumbZipPath ?? c.ImageZipPath)
                    });
                }
                break;

            case "characters":
                var mch = catSnap.Characters.Where(ch => Matches(q, ch.Name, ch.ZhName, ch.Index.ToString(CultureInfo.InvariantCulture), ch.Race, ch.Attribute, ch.StartingRelic)).ToList();
                total = mch.Count;
                foreach (var ch in mch.Skip(offset).Take(limit))
                {
                    itemsArr.Add((JsonNode?)new JsonObject
                    {
                        ["key"] = ch.Index.ToString(CultureInfo.InvariantCulture),
                        ["title"] = string.IsNullOrWhiteSpace(ch.ZhName) ? ch.Name : ch.ZhName,
                        ["subtitle"] = $"#{ch.Index} · {ch.Name}",
                        ["badge"] = ch.IsPlayable ? $"Playable · {ch.Cards.Count}" : $"NPC #{ch.Index}",
                        ["imageUrl"] = BuildVanillaImageUrl(ch.ImageZipPath)
                    });
                }
                break;

            case "relics":
                var mr = catSnap.Relics.Where(r => Matches(q, r.Key, r.Name, r.ZhName, r.Tag, r.Description, r.ZhDescription)).ToList();
                total = mr.Count;
                foreach (var r in mr.Skip(offset).Take(limit))
                {
                    itemsArr.Add((JsonNode?)new JsonObject
                    {
                        ["key"] = r.Key,
                        ["title"] = string.IsNullOrWhiteSpace(r.ZhName) ? r.Name : r.ZhName,
                        ["subtitle"] = r.Key,
                        ["badge"] = $"{r.Cost}G · {r.Tag}",
                        ["imageUrl"] = BuildVanillaImageUrl(r.ImageZipPath)
                    });
                }
                break;

            case "packs":
                var mp = catSnap.Packs.Where(p => Matches(q, p.Id, p.Name, p.ZhName, p.Code, p.Region)).ToList();
                total = mp.Count;
                foreach (var p in mp.Skip(offset).Take(limit))
                {
                    itemsArr.Add((JsonNode?)new JsonObject
                    {
                        ["key"] = p.Id,
                        ["title"] = string.IsNullOrWhiteSpace(p.ZhName) ? p.Name : p.ZhName,
                        ["subtitle"] = p.Id,
                        ["badge"] = $"{p.TotalCardCount} cards",
                        ["imageUrl"] = BuildVanillaImageUrl(p.ImageZipPath)
                    });
                }
                break;

            case "decks":
                var md = catSnap.PreconstructedDecks.Where(d => Matches(q, d.Id, d.Name, d.ZhName, d.Art)).ToList();
                total = md.Count;
                foreach (var d in md.Skip(offset).Take(limit))
                {
                    itemsArr.Add((JsonNode?)new JsonObject
                    {
                        ["key"] = d.Id,
                        ["title"] = string.IsNullOrWhiteSpace(d.ZhName) ? d.Name : d.ZhName,
                        ["subtitle"] = $"{d.Id} ({d.Art})",
                        ["badge"] = $"{d.CardIds.Count} cards",
                        ["imageUrl"] = BuildVanillaImageUrl(d.ImageZipPath)
                    });
                }
                break;
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["version"] = catSnap.Version,
            ["category"] = category,
            ["total"] = total,
            ["offset"] = offset,
            ["hasMore"] = offset + itemsArr.Count < total,
            ["counts"] = new JsonObject
            {
                ["cards"] = catSnap.Cards.Count,
                ["characters"] = catSnap.Characters.Count,
                ["relics"] = catSnap.Relics.Count,
                ["packs"] = catSnap.Packs.Count,
                ["decks"] = catSnap.PreconstructedDecks.Count
            },
            ["items"] = itemsArr
        };
    }

    private JsonObject HandleVanillaDetail(HttpListenerRequest req)
    {
        var catSnap = TryGetVanillaCatalog() ?? throw new ModOperationException("Vanilla catalog unavailable.");
        var category = Query(req, "category", "cards").ToLowerInvariant();
        var key = Query(req, "key");

        if (category == "cards" && int.TryParse(key, out var cid) && catSnap.CardById.TryGetValue(cid, out var c))
        {
            var lua = VanillaContentCatalog.ReadCardLuaScript(catSnap.ZipPath, cid) ?? string.Empty;
            return new JsonObject
            {
                ["ok"] = true,
                ["category"] = category,
                ["key"] = key,
                ["title"] = string.IsNullOrWhiteSpace(c.ZhName) ? c.Name : $"{c.ZhName} / {c.Name}",
                ["subtitle"] = $"ID: {c.Id} · {c.DisplayType} · {c.Race}" + (string.IsNullOrWhiteSpace(c.Attribute) ? "" : $" · {c.Attribute}"),
                ["meta"] = c.Level > 0 ? $"Level {c.Level} | ATK {c.Atk} / DEF {c.Def} | mask(type={c.TypeMask}, race=\"{c.RaceMask}\", attr={c.AttributeMask})" : $"mask(type={c.TypeMask}, race=\"{c.RaceMask}\", attr={c.AttributeMask})",
                ["description"] = string.IsNullOrWhiteSpace(c.ZhDesc) ? c.Desc : $"{c.ZhDesc}\n\n{c.Desc}",
                ["imageUrl"] = BuildVanillaImageUrl(c.ImageZipPath ?? c.ThumbZipPath),
                ["luaScript"] = lua,
                ["raw"] = new JsonObject
                {
                    ["id"] = c.Id, ["name"] = c.Name, ["zhName"] = c.ZhName, ["desc"] = c.Desc, ["zhDesc"] = c.ZhDesc,
                    ["displayType"] = c.DisplayType, ["typeMask"] = c.TypeMask, ["race"] = c.Race,
                    ["attribute"] = c.Attribute, ["level"] = c.Level, ["atk"] = c.Atk, ["def"] = c.Def
                }
            };
        }

        if (category == "characters" && int.TryParse(key, out var idx) && idx >= 0 && idx < catSnap.Characters.Count)
        {
            var ch = catSnap.Characters[idx];
            return new JsonObject
            {
                ["ok"] = true,
                ["category"] = category,
                ["key"] = key,
                ["title"] = string.IsNullOrWhiteSpace(ch.ZhName) ? ch.Name : $"{ch.ZhName} / {ch.Name}",
                ["subtitle"] = $"Index #{ch.Index} · Sprite: {ch.Sprite} · {ch.Race} / {ch.Attribute}",
                ["meta"] = $"Starting Relic: {ch.StartingRelic} | Packs: {string.Join(", ", ch.StartingPacks)}",
                ["description"] = $"Theme: {ch.Theme}\nCards ({ch.Cards.Count}): {string.Join(", ", ch.Cards)}",
                ["imageUrl"] = BuildVanillaImageUrl(ch.ImageZipPath),
                ["subCards"] = BuildSubCardsByNames(null, catSnap, ch.Cards),
                ["raw"] = new JsonObject
                {
                    ["name"] = ch.Name, ["zhName"] = ch.ZhName, ["sprite"] = ch.Sprite, ["race"] = ch.Race,
                    ["attribute"] = ch.Attribute, ["color"] = ch.Color, ["startingRelic"] = ch.StartingRelic,
                    ["startingPacks"] = ToStrArray(ch.StartingPacks), ["deckCards"] = ToStrArray(ch.Cards)
                }
            };
        }

        if (category == "relics")
        {
            var r = catSnap.Relics.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase))
                ?? throw new ModOperationException($"Relic not found: {key}");
            return new JsonObject
            {
                ["ok"] = true,
                ["category"] = category,
                ["key"] = key,
                ["title"] = string.IsNullOrWhiteSpace(r.ZhName) ? r.Name : $"{r.ZhName} / {r.Name}",
                ["subtitle"] = $"Key: {r.Key} · Cost: {r.Cost}G · Tag: {r.Tag}",
                ["meta"] = $"Art: {r.Art} | Effect: {r.Effect}",
                ["description"] = string.IsNullOrWhiteSpace(r.ZhDescription) ? r.Description : $"{r.ZhDescription}\n\n{r.Description}",
                ["imageUrl"] = BuildVanillaImageUrl(r.ImageZipPath),
                ["raw"] = new JsonObject
                {
                    ["key"] = r.Key, ["name"] = r.Name, ["zhName"] = r.ZhName, ["cost"] = r.Cost,
                    ["tag"] = r.Tag, ["description"] = r.Description, ["zhDescription"] = r.ZhDescription, ["art"] = r.Art
                }
            };
        }

        if (category == "packs")
        {
            var p = catSnap.Packs.FirstOrDefault(x => string.Equals(x.Id, key, StringComparison.OrdinalIgnoreCase))
                ?? throw new ModOperationException($"Pack not found: {key}");
            var ids = p.UltraIds.Concat(p.SuperIds).Concat(p.RareIds).Concat(p.CommonIds).Distinct().ToList();
            return new JsonObject
            {
                ["ok"] = true,
                ["category"] = category,
                ["key"] = key,
                ["title"] = string.IsNullOrWhiteSpace(p.ZhName) ? p.Name : $"{p.ZhName} / {p.Name}",
                ["subtitle"] = $"ID: {p.Id} · Code: {p.Code} · Region: {p.Region} · Size: {p.Size}",
                ["meta"] = $"Common: {p.CommonIds.Count} | Rare: {p.RareIds.Count} | Super: {p.SuperIds.Count} | Ultra: {p.UltraIds.Count}",
                ["description"] = $"Release Date: {p.Date} | Total Pool: {p.TotalCardCount} cards",
                ["imageUrl"] = BuildVanillaImageUrl(p.ImageZipPath),
                ["subCards"] = BuildSubCardsByIds(null, catSnap, ids),
                ["raw"] = new JsonObject
                {
                    ["id"] = p.Id, ["name"] = p.Name, ["zhName"] = p.ZhName, ["code"] = p.Code, ["size"] = p.Size,
                    ["commonIds"] = ToIntArray(p.CommonIds), ["rareIds"] = ToIntArray(p.RareIds),
                    ["superIds"] = ToIntArray(p.SuperIds), ["ultraIds"] = ToIntArray(p.UltraIds)
                }
            };
        }

        if (category == "decks")
        {
            var d = catSnap.PreconstructedDecks.FirstOrDefault(x => string.Equals(x.Id, key, StringComparison.OrdinalIgnoreCase))
                ?? throw new ModOperationException($"Deck not found: {key}");
            return new JsonObject
            {
                ["ok"] = true,
                ["category"] = category,
                ["key"] = key,
                ["title"] = string.IsNullOrWhiteSpace(d.ZhName) ? d.Name : $"{d.ZhName} / {d.Name}",
                ["subtitle"] = $"ID: {d.Id} · Art: {d.Art} · Date: {d.Date}",
                ["meta"] = $"Cards: {d.CardIds.Count}",
                ["description"] = string.Join(", ", d.CardIds.Take(40)),
                ["imageUrl"] = BuildVanillaImageUrl(d.ImageZipPath),
                ["subCards"] = BuildSubCardsByIds(null, catSnap, d.CardIds),
                ["raw"] = new JsonObject
                {
                    ["id"] = d.Id, ["name"] = d.Name, ["zhName"] = d.ZhName, ["art"] = d.Art,
                    ["cardIds"] = ToIntArray(d.CardIds)
                }
            };
        }

        throw new ModOperationException($"Item not found: {category}/{key}");
    }

    private async Task<JsonObject> HandleVanillaCloneAsync(JsonObject body)
    {
        var catSnap = TryGetVanillaCatalog() ?? throw new ModOperationException("Vanilla catalog unavailable.");
        var mod = ResolveManagedMod(Str(body, "modId"));
        var category = Str(body, "category").ToLowerInvariant();
        var key = Str(body, "key");
        var keepId = Bool(body, "keepVanillaIdentity", false);

        string clonedKey;
        if (category == "cards" && int.TryParse(key, out var cid) && catSnap.CardById.TryGetValue(cid, out var c))
        {
            var targetId = keepId ? c.Id : ModStudioService.SuggestNextCardId(mod.DirectoryPath);
            var lua = VanillaContentCatalog.ReadCardLuaScript(catSnap.ZipPath, c.Id);
            var artPath = VanillaContentCatalog.ExportZipImageToModArt(catSnap.ZipPath, c.ImageZipPath ?? c.ThumbZipPath, mod.DirectoryPath, targetId.ToString(CultureInfo.InvariantCulture));
            var preset = ModStudioService.CardTypePresets.FirstOrDefault(p => p.TypeMask == c.TypeMask) ?? ModStudioService.CardTypePresets[0];
            ModStudioService.SaveVisualCard(
                mod.DirectoryPath, targetId,
                keepId ? c.Name : $"{c.Name} (Custom)",
                string.IsNullOrWhiteSpace(c.ZhName) ? null : (keepId ? c.ZhName : $"{c.ZhName}（改）"),
                c.Desc, NullIfBlank(c.ZhDesc), preset, c.Race, c.Attribute, c.Level, c.Atk, c.Def, null, artPath, lua);
            clonedKey = targetId.ToString(CultureInfo.InvariantCulture);
        }
        else if (category == "characters" && int.TryParse(key, out var idx) && idx >= 0 && idx < catSnap.Characters.Count)
        {
            var ch = catSnap.Characters[idx];
            var newName = keepId ? ch.Name : $"{ch.Name} Remix";
            ModStudioService.SaveVisualCharacter(
                mod.DirectoryPath, newName,
                string.IsNullOrWhiteSpace(ch.ZhName) ? null : (keepId ? ch.ZhName : $"{ch.ZhName}（改）"),
                ch.Sprite, ch.Race, ch.Attribute, ch.Color, ch.StartingRelic,
                string.Join(", ", ch.StartingPacks), string.Join(", ", ch.Cards), true);
            clonedKey = ModRepository.SanitizeId(newName);
        }
        else if (category == "relics")
        {
            var r = catSnap.Relics.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase))
                ?? throw new ModOperationException($"Relic not found: {key}");
            var newKey = keepId ? r.Key : $"{r.Key}_custom";
            var artPath = VanillaContentCatalog.ExportZipImageToModArt(catSnap.ZipPath, r.ImageZipPath, mod.DirectoryPath, $"relic_{newKey}");
            ModStudioService.SaveVisualRelic(
                mod.DirectoryPath, newKey,
                keepId ? r.Name : $"{r.Name} (Custom)",
                string.IsNullOrWhiteSpace(r.ZhName) ? null : (keepId ? r.ZhName : $"{r.ZhName}（改）"),
                r.Cost, r.Tag, r.Description, NullIfBlank(r.ZhDescription), artPath ?? r.Art, ModStudioService.BuildLuaRelicTemplate("gain_lp"));
            clonedKey = ModRepository.SanitizeId(newKey).Replace('-', '_');
        }
        else if (category == "packs")
        {
            var p = catSnap.Packs.FirstOrDefault(x => string.Equals(x.Id, key, StringComparison.OrdinalIgnoreCase))
                ?? throw new ModOperationException($"Pack not found: {key}");
            var newId = keepId ? p.Id : $"{p.Id}-MOD";
            ModStudioService.SaveVisualPack(
                mod.DirectoryPath, newId,
                keepId ? p.Name : $"{p.Name} (Custom)",
                string.IsNullOrWhiteSpace(p.ZhName) ? null : (keepId ? p.ZhName : $"{p.ZhName}（改）"),
                p.Code, p.Size,
                string.Join(", ", p.CommonIds), string.Join(", ", p.RareIds),
                string.Join(", ", p.SuperIds), string.Join(", ", p.UltraIds));
            clonedKey = newId;
        }
        else if (category == "decks")
        {
            var d = catSnap.PreconstructedDecks.FirstOrDefault(x => string.Equals(x.Id, key, StringComparison.OrdinalIgnoreCase))
                ?? throw new ModOperationException($"Deck not found: {key}");
            var newId = keepId ? d.Id : $"{d.Id}-MOD";
            ModStudioService.SaveVisualPreconstructedDeck(
                mod.DirectoryPath, newId,
                keepId ? d.Name : $"{d.Name} (Custom)",
                string.IsNullOrWhiteSpace(d.ZhName) ? null : (keepId ? d.ZhName : $"{d.ZhName}（改）"),
                d.Art, string.Join(", ", d.CardIds));
            clonedKey = newId;
        }
        else
        {
            throw new ModOperationException($"Unsupported clone target: {category}/{key}");
        }

        InvalidateStudioCache();
        EmitLog(LogLevel.Ok, $"Cloned vanilla {category}/{key} -> {mod.Id}/{clonedKey}");
        await TriggerHotReloadIfRunningAsync($"clone-{clonedKey}");
        return new JsonObject { ["ok"] = true, ["category"] = category, ["clonedKey"] = clonedKey };
    }

    // ==================== NATIVE OS DIALOGS & SHELL ====================

    private JsonObject HandlePickFolder(JsonObject body)
    {
        var picked = _pickFolderOnUi(Str(body, "title", "Select Folder"), NullIfBlank(Str(body, "initialPath")));
        return new JsonObject { ["ok"] = true, ["path"] = picked };
    }

    private JsonObject HandlePickFile(JsonObject body)
    {
        var kind = Str(body, "kind", "zip").ToLowerInvariant();
        (string, string[])[] filters = kind switch
        {
            "image" => [("Images", ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.svg"]), ("All Files", ["*.*"])],
            "ydk" => [("YGOPro Deck", ["*.ydk"]), ("All Files", ["*.*"])],
            "ypk" => [("YGOPro / MDPro3 Expansion (.ypk)", ["*.ypk", "*.zip"]), ("All Files", ["*.*"])],
            _ => [("MOD / YRDPK / YPK Archive", ["*.yrdpk", "*.zip", "*.ypk"]), ("YGO Rogue MOD Package (.yrdpk)", ["*.yrdpk"]), ("YGOPro Expansion (.ypk)", ["*.ypk"]), ("ZIP Archive", ["*.zip"]), ("All Files", ["*.*"])]
        };
        var picked = _pickOpenFileOnUi(Str(body, "title", "Select File"), filters);
        return new JsonObject { ["ok"] = true, ["path"] = picked };
    }

    private JsonObject HandleSaveZipDialog(JsonObject body)
    {
        var defaultDir = NullIfBlank(Str(body, "initialPath"))
                         ?? Path.Combine(GetEngine().Repository.WorkspaceRoot, "dist");
        Directory.CreateDirectory(defaultDir);
        var suggested = Str(body, "suggestedName", "mod-pack.yrdpk");
        var picked = _pickSaveZipOnUi(Str(body, "title", "Save MOD Package (.yrdpk / .zip)"), suggested, defaultDir);
        return new JsonObject { ["ok"] = true, ["path"] = picked };
    }

    private JsonObject HandleOpenFolder(JsonObject body)
    {
        var path = Str(body, "path");
        if (string.IsNullOrWhiteSpace(path)) path = GetEngine().Repository.WorkspaceRoot;
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{Path.GetFullPath(path)}\"", UseShellExecute = true });
        return new JsonObject { ["ok"] = true };
    }

    private JsonObject HandleOpenUrl(JsonObject body)
    {
        var url = Str(body, "url");
        if (!string.IsNullOrWhiteSpace(url))
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        return new JsonObject { ["ok"] = true };
    }

    // ==================== IMAGE STREAMING & EMBEDDED FRONTEND ====================

    private async Task HandleVanillaImageAsync(HttpListenerRequest req, HttpListenerResponse res, CancellationToken ct)
    {
        var entry = Query(req, "entry");
        var zipPath = TryGetVanillaZipPath();
        var bytes = VanillaContentCatalog.ReadZipEntryBytes(zipPath ?? string.Empty, entry);
        if (bytes is null || bytes.Length == 0)
        {
            res.StatusCode = 404;
            res.Close();
            return;
        }

        res.ContentType = GuessMime(entry);
        res.Headers["Cache-Control"] = "public, max-age=86400";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes, ct);
        res.Close();
    }

    private async Task HandleStudioImageAsync(HttpListenerRequest req, HttpListenerResponse res, CancellationToken ct)
    {
        var modId = Query(req, "modId");
        var localRel = Query(req, "local");
        var absPath = Query(req, "absPath");
        var vanillaEntry = Query(req, "vanilla");
        var relicKey = Query(req, "relicKey");

        byte[]? bytes = null;
        string mime = "image/jpeg";

        if (!string.IsNullOrWhiteSpace(absPath) && File.Exists(absPath))
        {
            var ext = Path.GetExtension(absPath).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif")
            {
                bytes = await File.ReadAllBytesAsync(absPath, ct);
                mime = GuessMime(absPath);
            }
        }
        else if (!string.IsNullOrWhiteSpace(localRel) && !string.IsNullOrWhiteSpace(modId))
        {
            var mod = ResolveManagedMod(modId);
            var full = Path.GetFullPath(Path.Combine(mod.DirectoryPath, localRel.Replace('/', Path.DirectorySeparatorChar)));
            if (full.StartsWith(Path.GetFullPath(mod.DirectoryPath), StringComparison.OrdinalIgnoreCase) && File.Exists(full))
            {
                bytes = await File.ReadAllBytesAsync(full, ct);
                mime = GuessMime(full);
            }
        }
        else if (!string.IsNullOrWhiteSpace(relicKey) && !string.IsNullOrWhiteSpace(modId))
        {
            var snap = GetOrLoadStudioSnapshot(modId);
            var relic = snap.Relics.FirstOrDefault(r => string.Equals(r.Key, relicKey, StringComparison.OrdinalIgnoreCase));
            if (relic is not null)
            {
                bytes = ModStudioCatalog.LoadImageBytes(relic.Image, TryGetVanillaZipPath());
                mime = relic.Image.DataUri?.Contains("png", StringComparison.OrdinalIgnoreCase) == true ? "image/png" : "image/webp";
            }
        }
        else if (!string.IsNullOrWhiteSpace(vanillaEntry))
        {
            bytes = VanillaContentCatalog.ReadZipEntryBytes(TryGetVanillaZipPath() ?? string.Empty, vanillaEntry);
            mime = GuessMime(vanillaEntry);
        }

        if (bytes is null || bytes.Length == 0)
        {
            res.StatusCode = 404;
            res.Close();
            return;
        }

        res.ContentType = mime;
        res.Headers["Cache-Control"] = "public, max-age=120";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes, ct);
        res.Close();
    }

    private async Task ServeStaticFrontendAsync(string path, HttpListenerResponse res, CancellationToken ct)
    {
        var rel = path.TrimStart('/');
        if (string.IsNullOrWhiteSpace(rel)) rel = "index.html";

        // 1. Check disk wwwroot next to exe (dev convenience)
        var diskFile = Path.Combine(AppContext.BaseDirectory, "wwwroot", rel.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(diskFile))
        {
            var data = await File.ReadAllBytesAsync(diskFile, ct);
            res.ContentType = GuessMime(rel);
            res.Headers["Cache-Control"] = rel.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ? "no-cache" : "public, max-age=3600";
            res.ContentLength64 = data.Length;
            await res.OutputStream.WriteAsync(data, ct);
            res.Close();
            return;
        }

        // 2. Check embedded assembly resources (single-file Native AOT release!)
        if (!_embeddedResources.TryGetValue(rel, out var resName))
        {
            _embeddedResources.TryGetValue("index.html", out resName);
            rel = "index.html";
        }

        if (resName is not null)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resName);
            if (stream is not null)
            {
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);
                var data = ms.ToArray();
                res.ContentType = GuessMime(rel);
                res.Headers["Cache-Control"] = rel.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ? "no-cache" : "public, max-age=3600";
                res.ContentLength64 = data.Length;
                await res.OutputStream.WriteAsync(data, ct);
                res.Close();
                return;
            }
        }

        res.StatusCode = 404;
        var msg = Encoding.UTF8.GetBytes("Frontend bundle not found. Run `npm run build` in frontend/ to populate wwwroot/.");
        res.ContentType = "text/plain; charset=utf-8";
        await res.OutputStream.WriteAsync(msg, ct);
        res.Close();
    }

    // ==================== SSE STREAMING ====================

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
            await client.SendAsync("connected", new JsonObject { ["port"] = Port }.ToJsonString(ModPatcher.JsonOptions), ct);
            while (!ct.IsCancellationRequested && !client.IsClosed)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
                await client.SendCommentAsync("ping", ct);
            }
        }
        catch { }
        finally
        {
            _sseClients.TryRemove(id, out _);
            client.Dispose();
        }
    }

    private void EmitLog(LogLevel level, string message)
    {
        BroadcastSse("log", new JsonObject
        {
            ["time"] = DateTime.Now.ToString("HH:mm:ss"),
            ["level"] = level.ToString().ToLowerInvariant(),
            ["message"] = message
        });
    }

    private void EmitProgress(string status, double? percent)
    {
        _lastProgressStatus = status;
        _lastProgressPercent = percent;
        BroadcastSse("progress", new JsonObject
        {
            ["status"] = status,
            ["percent"] = percent,
            ["isDownloading"] = _activeDownloadCts is not null,
            ["label"] = _activeDownloadLabel
        });
    }

    private void BroadcastSse(string eventName, JsonObject payload)
    {
        var json = payload.ToJsonString(ModPatcher.JsonOptions);
        foreach (var (id, client) in _sseClients)
        {
            _ = Task.Run(async () =>
            {
                try { await client.SendAsync(eventName, json, CancellationToken.None); }
                catch
                {
                    _sseClients.TryRemove(id, out _);
                    client.Dispose();
                }
            });
        }
    }

    // ==================== HELPERS ====================

    private ModInjectionEngine GetEngine() { lock (_engineLock) return _engine; }

    private void SwitchEngineWorkspace(string workspacePath)
    {
        StopHotReloadInternal();
        lock (_engineLock) _engine = new ModInjectionEngine(workspacePath);
        InvalidateCaches();
    }

    private void InvalidateCaches()
    {
        _vanillaCache = null;
        _vanillaCacheZipPath = null;
        InvalidateStudioCache();
    }

    private void InvalidateStudioCache()
    {
        _studioCache = null;
        _studioCacheKey = null;
    }

    private async Task TriggerHotReloadIfRunningAsync(string reason)
    {
        if (_hotReloadServer is { IsRunning: true } srv)
            await srv.TriggerManualReloadAsync(reason);
    }

    private ManagedMod ResolveManagedMod(string modId) =>
        GetEngine().Repository.LoadMods()
            .FirstOrDefault(m => string.Equals(m.Id, modId, StringComparison.OrdinalIgnoreCase))
        ?? throw new ModOperationException($"找不到 MOD：{modId}");

    private static string? BuildStudioImageUrl(ModStudioSnapshot snap, StudioImageRef img, string? relicKeyForDataUri = null)
    {
        if (!string.IsNullOrWhiteSpace(img.LocalFilePath))
        {
            var rel = Path.GetRelativePath(snap.ModDir, img.LocalFilePath).Replace('\\', '/');
            return $"/api/studio/image?modId={Uri.EscapeDataString(snap.ModId)}&local={Uri.EscapeDataString(rel)}";
        }
        if (!string.IsNullOrWhiteSpace(img.DataUri) && !string.IsNullOrWhiteSpace(relicKeyForDataUri))
            return $"/api/studio/image?modId={Uri.EscapeDataString(snap.ModId)}&relicKey={Uri.EscapeDataString(relicKeyForDataUri)}";
        if (!string.IsNullOrWhiteSpace(img.VanillaZipEntry))
            return BuildVanillaImageUrl(img.VanillaZipEntry);
        return null;
    }

    private static string? BuildVanillaImageUrl(string? entry) =>
        string.IsNullOrWhiteSpace(entry) ? null : $"/api/vanilla/image?entry={Uri.EscapeDataString(entry)}";

    private JsonArray BuildSubCardsByIds(ModStudioSnapshot? snap, VanillaCatalogSnapshot? vanilla, IReadOnlyList<int> ids)
    {
        var arr = new JsonArray();
        foreach (var id in ids.Take(24))
        {
            var modCard = snap?.Cards.FirstOrDefault(c => c.Id == id);
            if (modCard is not null && snap is not null)
            {
                arr.Add((JsonNode?)new JsonObject
                {
                    ["id"] = id,
                    ["name"] = string.IsNullOrWhiteSpace(modCard.ZhName) ? modCard.Name : modCard.ZhName,
                    ["imageUrl"] = BuildStudioImageUrl(snap, modCard.Image)
                });
            }
            else if (vanilla is not null && vanilla.CardById.TryGetValue(id, out var vc))
            {
                arr.Add((JsonNode?)new JsonObject
                {
                    ["id"] = id,
                    ["name"] = string.IsNullOrWhiteSpace(vc.ZhName) ? vc.Name : vc.ZhName,
                    ["imageUrl"] = BuildVanillaImageUrl(vc.ThumbZipPath ?? vc.ImageZipPath)
                });
            }
        }
        return arr;
    }

    private JsonArray BuildSubCardsByNames(ModStudioSnapshot? snap, VanillaCatalogSnapshot? vanilla, IReadOnlyList<string> names)
    {
        var arr = new JsonArray();
        foreach (var name in names.Take(24))
        {
            var modCard = snap?.Cards.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (modCard is not null && snap is not null)
            {
                arr.Add((JsonNode?)new JsonObject
                {
                    ["id"] = modCard.Id,
                    ["name"] = string.IsNullOrWhiteSpace(modCard.ZhName) ? modCard.Name : modCard.ZhName,
                    ["imageUrl"] = BuildStudioImageUrl(snap, modCard.Image)
                });
            }
            else if (vanilla is not null && vanilla.CardByName.TryGetValue(name, out var vc))
            {
                arr.Add((JsonNode?)new JsonObject
                {
                    ["id"] = vc.Id,
                    ["name"] = string.IsNullOrWhiteSpace(vc.ZhName) ? vc.Name : vc.ZhName,
                    ["imageUrl"] = BuildVanillaImageUrl(vc.ThumbZipPath ?? vc.ImageZipPath)
                });
            }
        }
        return arr;
    }

    private JsonObject BuildApiDocsPayload(string lang = "zh")
    {
        var isEn = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);
        JsonObject ep(string method, string path, string zhDesc, string enDesc) => new()
        {
            ["method"] = method,
            ["path"] = path,
            ["description"] = isEn ? enDesc : zhDesc
        };

        return new JsonObject
        {
            ["ok"] = true,
            ["baseUrl"] = BaseUrl,
            ["endpoints"] = new JsonArray(
                ep("GET", "/api/status", "获取 MOD 管理器完整状态、已安装 MOD、冲突检测、版本兼容与热加载信息", "Get full MOD manager status, mods, conflicts, version issues & hot-reload state"),
                ep("GET", "/api/events", "SSE 实时事件流（log / progress / hotreload）", "SSE real-time stream (events: log, progress, hotreload)"),
                ep("GET", "/api/workspaces", "列出所有已注册的 MOD 工作区", "List registered MOD workspaces"),
                ep("POST", "/api/workspaces/create", "创建并切换到新的 MOD 工作区", "Create and switch to a new MOD workspace"),
                ep("POST", "/api/workspaces/switch", "切换当前激活的 MOD 工作区", "Switch active MOD workspace"),
                ep("POST", "/api/mods/create", "创建新的 Mod Spec v1 模板或完整示例 MOD", "Create a new Mod Spec v1 template or full example MOD"),
                ep("POST", "/api/mods/create-example", "一键生成含封面、自动导入双 JS/CSS、F9 游戏内设置与全套脚本的完整示例 MOD", "Create a full showcase example MOD with cover, auto JS/CSS, F9 settings & scripts"),
                ep("POST", "/api/mods/metadata", "更新 MOD 分类、封面、作者、标签与自动导入 Entries 选项", "Update MOD category, cover, author, tags & autoDiscoverEntries"),
                ep("GET", "/api/mods/cover", "流式读取 MOD 封面图片（支持 .png/.webp/.jpg/.svg 与自动回退）", "Stream resolved MOD cover image (.png/.webp/.jpg/.svg)"),
                ep("POST", "/api/mods/import", "导入单个 MOD（.zip / 文件夹）、YGOPro/MDPro3 扩展包（.ypk）或工作区整合包", "Import a MOD (.zip/folder), YGOPro/MDPro3 expansion (.ypk), or Workspace Modpack"),
                ep("POST", "/api/mods/import-upload", "通过拖拽上传二进制 .zip / .ypk 导入 MOD、扩展卡包或工作区整合包", "Drag-and-drop binary .zip/.ypk upload to import MOD, YPK expansion, or Workspace Pack"),
                ep("POST", "/api/mods/cover-upload", "通过拖拽上传图片直接更换指定 MOD 封面", "Drag-and-drop binary image upload to update a MOD cover"),
                ep("POST", "/api/apply", "合成并注入所有已启用 MOD（零侵入虚拟版本包或启动器配置模式）", "Apply all enabled MODs (Zero-Touch VirtualBundle or LauncherManifest)"),
                ep("POST", "/api/restore", "一键无痕还原至纯净原版游戏", "Restore clean vanilla game"),
                ep("POST", "/api/hotreload/start", "启动本地 HTTP/SSE MOD 热加载开发服务器与文件监听", "Start live HTTP/SSE MOD Hot-Reload server + FileSystemWatcher"),
                ep("POST", "/api/hotreload/inject", "向桌面版游戏虚拟版本包注入热加载桥接脚本", "Inject hot-reload bridge into game virtual bundle"),
                ep("GET", "/api/studio/catalog", "分页查询 MOD 工作台可视化图鉴（卡牌/角色/遗物/卡包/卡组/战役/代码）", "Paginated MOD Studio gallery items (cards/characters/relics/packs/decks/campaign/code)"),
                ep("GET", "/api/studio/campaign", "读取 MOD 战役规则、原版删减与系统开关配置（campaign-config.json）", "Read MOD campaign rules, vanilla removals & system toggles (campaign-config.json)"),
                ep("POST", "/api/studio/campaign/save", "保存 MOD 战役规则与原版内容删减配置并触发热加载", "Save MOD campaign rules & vanilla removals config and trigger hot-reload"),
                ep("POST", "/api/export/linux-bundle", "导出 Linux / Steam Deck 零侵入虚拟版本包与一键安装脚本", "Export Linux / Steam Deck zero-touch virtual bundle + installer script"),
                ep("POST", "/api/export/android-bundle", "导出 Android MOD 补丁包或直接修补未签名 APK（assets/web/*）", "Export Android MOD overlay ZIP or directly patch an unsigned APK (assets/web/*)"),
                ep("GET", "/api/studio/file", "读取 MOD 目录内的任意文本 / Lua / JS / DSL 文件", "Read any text/Lua/JS/DSL file inside a MOD"),
                ep("POST", "/api/studio/file", "保存 MOD 内文件并自动触发热加载", "Save file inside a MOD & trigger hot-reload"),
                ep("GET", "/api/vanilla/catalog", "分页浏览本地离线原版图鉴（无限滚动）", "Paginated offline vanilla game gallery (infinite scroll)"),
                ep("GET", "/api/vanilla/detail", "获取原版条目完整详情、关联卡图与 ocgcore Lua 脚本", "Vanilla entity detail + sub-cards + ocgcore Lua script"),
                ep("GET", "/api/vanilla/image", "直接从原版 web-X.Y.Z.zip 流式读取原版高清卡图与立绘", "Stream vanilla artwork image directly from web-X.Y.Z.zip"),
                ep("POST", "/api/vanilla/clone", "一键克隆原版条目、原图与 Lua 脚本到当前 MOD", "Clone vanilla entity + artwork + Lua script directly into a MOD"),
                ep("POST", "/mcp", "内置 MCP Server（Streamable HTTP 协议，23 个 MOD/脚本/YPK/热加载工具）", "Built-in MCP Server (Streamable HTTP transport, 23 MOD/script/YPK/hot-reload tools)"),
                ep("GET", "/mcp/sse", "内置 MCP Server（传统 HTTP+SSE 兼容传输端点）", "Built-in MCP Server (Legacy HTTP+SSE transport endpoint)"),
                ep("GET", "/api/mcp/info", "获取内置 MCP 连接地址、客户端配置片段、工具列表与实时调用日志", "Get built-in MCP URLs, client snippets, tools & live call history"),
                ep("POST", "/api/mcp/install-config", "一键将内置 MCP 写入工作区 .mcp.json", "Write built-in MCP server config into workspace .mcp.json"),
                ep("GET", "/api/updates/check", "检查原版游戏与管理器更新（默认官方 GitHub，可选代理加速）", "Check official game & manager updates (official GitHub or optional proxy)"),
                ep("POST", "/api/updates/download-game", "断点续传下载原版数据包（web-.zip）或完整安装包（.exe）", "Resumable HTTP Range download for vanilla web-.zip or full installer"),
                ep("GET", "/api/vanilla/dll-info", "读取并检视原版 Yu-Gi-Oh! Rogue Duelist.dll 元数据与更新机制", "Inspect vanilla Yu-Gi-Oh! Rogue Duelist.dll metadata, SHA-256 & update endpoints"))
        };
    }

    private static async Task<JsonObject> ReadJsonBodyAsync(HttpListenerRequest req, CancellationToken ct)
    {
        using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
        var raw = await reader.ReadToEndAsync(ct);
        if (string.IsNullOrWhiteSpace(raw)) return new JsonObject();
        return JsonNode.Parse(raw) as JsonObject ?? new JsonObject();
    }

    private static async Task WriteJsonAsync(HttpListenerResponse res, int statusCode, JsonNode node, CancellationToken ct)
    {
        var bytes = ModPatcher.GetUtf8Bytes(node.ToJsonString(ModPatcher.JsonOptions));
        res.StatusCode = statusCode;
        res.ContentType = "application/json; charset=utf-8";
        res.Headers["Cache-Control"] = "no-store";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes, ct);
        res.Close();
    }

    private static string Query(HttpListenerRequest req, string key, string fallback = "") =>
        req.QueryString[key] ?? fallback;

    private static int QueryInt(HttpListenerRequest req, string key, int fallback) =>
        int.TryParse(req.QueryString[key], out var v) ? v : fallback;

    private static string Str(JsonObject obj, string key, string fallback = "") =>
        obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? (s ?? fallback) : (obj[key]?.ToString() ?? fallback);

    private static bool Bool(JsonObject obj, string key, bool fallback = false) =>
        obj[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    private static int IntVal(JsonObject obj, string key, int fallback = 0) =>
        obj[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i
        : int.TryParse(Str(obj, key), out var p) ? p : fallback;

    private static int? NullableInt(JsonObject obj, string key) =>
        obj[key] is null ? null : (int.TryParse(Str(obj, key), out var p) ? p : null);

    private static double? NullableDouble(JsonObject obj, string key) =>
        obj[key] is null ? null : (double.TryParse(Str(obj, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null);

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static JsonArray ToStrArray(IEnumerable<string> items) =>
        new(items.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());

    private static JsonArray ToIntArray(IEnumerable<int> items) =>
        new(items.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());

    private static bool Matches(string query, params string?[] fields)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        foreach (var f in fields)
            if (!string.IsNullOrWhiteSpace(f) && f.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string GuessMime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "application/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        _ => "application/octet-stream"
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopHotReloadInternal();
        try { _activeDownloadCts?.Cancel(); } catch { }
        _cts?.Cancel();
        foreach (var kv in _sseClients) kv.Value.Dispose();
        _sseClients.Clear();
        foreach (var kv in _mcpSseClients) kv.Value.Dispose();
        _mcpSseClients.Clear();
        try { _listener?.Stop(); _listener?.Close(); } catch { }
        _cts?.Dispose();
    }

    private sealed class SseClient(HttpListenerResponse response) : IDisposable
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        public bool IsClosed { get; private set; }

        public async Task SendAsync(string eventName, string data, CancellationToken ct)
        {
            var payload = $"event: {eventName}\ndata: {data.Replace("\r", "").Replace("\n", "\ndata: ")}\n\n";
            await WriteRawAsync(payload, ct);
        }

        public Task SendCommentAsync(string comment, CancellationToken ct) => WriteRawAsync($": {comment}\n\n", ct);

        private async Task WriteRawAsync(string text, CancellationToken ct)
        {
            if (IsClosed) return;
            await _lock.WaitAsync(ct);
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
            finally { _lock.Release(); }
        }

        public void Dispose()
        {
            if (IsClosed) return;
            IsClosed = true;
            try { response.Close(); } catch { }
            _lock.Dispose();
        }
    }

    private sealed class SseInstallerInteraction(Action<LogLevel, string> log, Action<string, double?> progress) : IInstallerInteraction
    {
        public void Log(LogLevel level, string message) => log(level, message);
        public void ReportProgress(string status, double? percent = null) => progress(status, percent);
        public Task<bool> ConfirmAsync(string prompt, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string?> PromptForPicsDirAsync(string prompt, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
}
