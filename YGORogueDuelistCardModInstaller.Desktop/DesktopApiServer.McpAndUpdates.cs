using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using YGORogueDuelistCardModInstaller.Core;
using YGORogueDuelistCardModInstaller.Core.Mcp;
using YGORogueDuelistCardModInstaller.Core.ModManager;

namespace YGORogueDuelistCardModInstaller.Desktop;

public sealed partial class DesktopApiServer
{
    private McpHostHooks BuildMcpHooks() => new()
    {
        GetDefaultWorkspaceRoot = () => GetEngine().Repository.WorkspaceRoot,
        GetStudioFocus = () => _studioFocus,
        OnModMutated = (modId, relPath, toolName) =>
        {
            InvalidateStudioCache();
            EmitLog(LogLevel.Ok, $"[MCP:{toolName}] 已更新 MOD '{modId}' ({relPath ?? "files"})");
            BroadcastSse("studio-sync", new JsonObject
            {
                ["modId"] = modId,
                ["path"] = relPath,
                ["tool"] = toolName,
                ["time"] = DateTime.Now.ToString("HH:mm:ss")
            });
            TriggerHotReloadBackground($"mcp:{toolName}:{relPath ?? modId}");
        },
        OnToolCalled = record =>
        {
            _mcpCallHistory.Enqueue(record);
            while (_mcpCallHistory.Count > 80) _mcpCallHistory.TryDequeue(out _);

            BroadcastSse("mcp-call", SerializeMcpCall(record));
        },
        OnTriggerHotReloadAsync = async (mode, reason, ct) =>
        {
            if (_hotReloadServer is not { IsRunning: true })
            {
                if (string.Equals(mode, "start", StringComparison.OrdinalIgnoreCase))
                {
                    await StartHotReloadInternalAsync(ModHotReloadServer.DefaultPort, ct);
                    return $"Hot-Reload started at {_hotReloadServer?.BaseUrl} (rev #{_hotReloadServer?.Revision}).";
                }
                return "Hot-Reload server is currently stopped.";
            }

            var info = await _hotReloadServer.TriggerManualReloadAsync(reason ?? "MCP trigger_hot_reload", ct);
            return $"Hot-Reload completed: rev #{info.Revision} ({info.AppliedModCount} MODs, cssOnly={info.CssOnly}, url={_hotReloadServer.BaseUrl}).";
        }
    };

    // ==================== BUILT-IN MCP STREAMABLE HTTP & LEGACY SSE ====================

    private async Task HandleMcpStreamableHttpAsync(HttpListenerRequest req, HttpListenerResponse res, CancellationToken ct)
    {
        var sessionId = req.Headers["Mcp-Session-Id"];
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            sessionId = Guid.NewGuid().ToString("N");
        }
        res.Headers["Mcp-Session-Id"] = sessionId;

        if (string.Equals(req.HttpMethod, "DELETE", StringComparison.OrdinalIgnoreCase))
        {
            if (_mcpSseClients.TryRemove(sessionId, out var old)) old.Dispose();
            res.StatusCode = 200;
            res.Close();
            return;
        }

        if (string.Equals(req.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
        {
            // Optional server-initiated SSE stream per MCP Streamable HTTP spec (2025-03-26)
            res.StatusCode = 200;
            res.ContentType = "text/event-stream; charset=utf-8";
            res.Headers["Cache-Control"] = "no-cache";
            res.Headers["Connection"] = "keep-alive";

            var client = new SseClient(res);
            _mcpSseClients[sessionId] = client;
            try
            {
                await client.SendCommentAsync($"mcp-connected:{sessionId}", ct);
                while (!ct.IsCancellationRequested && !client.IsClosed)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), ct);
                    await client.SendCommentAsync("ping", ct);
                }
            }
            catch { }
            finally
            {
                _mcpSseClients.TryRemove(sessionId, out _);
                client.Dispose();
            }
            return;
        }

        if (!string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            res.StatusCode = 405;
            res.Close();
            return;
        }

        using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
        var rawBody = await reader.ReadToEndAsync(ct);
        if (string.IsNullOrWhiteSpace(rawBody))
        {
            var err = McpProtocolHandler.BuildError(null, -32700, "Empty JSON-RPC body.");
            await WriteJsonAsync(res, 400, err, ct);
            return;
        }

        var reply = await McpProtocolHandler.HandleRawJsonAsync(rawBody, BuildMcpHooks(), ct);
        if (reply is null)
        {
            // Notifications return 202 Accepted with empty body per MCP Streamable HTTP spec
            res.StatusCode = 202;
            res.Close();
            return;
        }

        await WriteJsonAsync(res, 200, reply, ct);
    }

    private async Task HandleMcpLegacySseAsync(HttpListenerResponse res, CancellationToken ct)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        res.StatusCode = 200;
        res.ContentType = "text/event-stream; charset=utf-8";
        res.Headers["Cache-Control"] = "no-cache";
        res.Headers["Connection"] = "keep-alive";

        var client = new SseClient(res);
        _mcpSseClients[sessionId] = client;
        try
        {
            await client.SendAsync("endpoint", $"/mcp/messages?sessionId={sessionId}", ct);
            while (!ct.IsCancellationRequested && !client.IsClosed)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
                await client.SendCommentAsync("ping", ct);
            }
        }
        catch { }
        finally
        {
            _mcpSseClients.TryRemove(sessionId, out _);
            client.Dispose();
        }
    }

    private async Task HandleMcpLegacyMessagesAsync(HttpListenerRequest req, HttpListenerResponse res, CancellationToken ct)
    {
        var sessionId = Query(req, "sessionId");
        using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
        var rawBody = await reader.ReadToEndAsync(ct);
        var reply = await McpProtocolHandler.HandleRawJsonAsync(rawBody, BuildMcpHooks(), ct);

        if (reply is not null && !string.IsNullOrWhiteSpace(sessionId) && _mcpSseClients.TryGetValue(sessionId, out var client))
        {
            try
            {
                await client.SendAsync("message", reply.ToJsonString(ModPatcher.JsonOptions), ct);
            }
            catch
            {
                _mcpSseClients.TryRemove(sessionId, out _);
                client.Dispose();
            }
            res.StatusCode = 202;
            res.Close();
            return;
        }

        if (reply is null)
        {
            res.StatusCode = 202;
            res.Close();
            return;
        }

        await WriteJsonAsync(res, 200, reply, ct);
    }

    // ==================== STUDIO FOCUS & BUILT-IN MCP HUB REST API ====================

    private JsonObject HandleUpdateStudioContext(JsonObject body)
    {
        var engine = GetEngine();
        _studioFocus = new StudioFocusContext
        {
            WorkspaceRoot = engine.Repository.WorkspaceRoot,
            ModId = NullIfBlank(Str(body, "modId")),
            ModName = NullIfBlank(Str(body, "modName")),
            Category = NullIfBlank(Str(body, "category")),
            ItemKey = NullIfBlank(Str(body, "itemKey")),
            ItemName = NullIfBlank(Str(body, "itemName")),
            ScriptPath = NullIfBlank(Str(body, "scriptPath")),
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        return new JsonObject { ["ok"] = true };
    }

    private JsonObject BuildMcpInfoPayload()
    {
        var baseTrim = BaseUrl.TrimEnd('/');
        var httpUrl = $"{baseTrim}/mcp";
        var sseUrl = $"{baseTrim}/mcp/sse";
        var exePath = Environment.ProcessPath ?? "YGORogueDuelistCardModInstaller.Desktop.exe";
        var mcpExeCandidate = Path.Combine(AppContext.BaseDirectory, "YGORogueDuelistCardModInstaller.Mcp.exe");
        var stdioCmd = File.Exists(mcpExeCandidate) ? mcpExeCandidate : exePath;
        var stdioArgs = File.Exists(mcpExeCandidate) ? Array.Empty<string>() : new[] { "--mcp" };

        var historyArr = new JsonArray();
        foreach (var rec in _mcpCallHistory.Reverse())
        {
            historyArr.Add((JsonNode?)SerializeMcpCall(rec));
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["serverName"] = McpProtocolHandler.ServerName,
            ["serverVersion"] = McpProtocolHandler.ServerVersion,
            ["protocolVersion"] = McpProtocolHandler.DefaultProtocolVersion,
            ["httpUrl"] = httpUrl,
            ["sseUrl"] = sseUrl,
            ["stdioCommand"] = stdioCmd,
            ["stdioArgs"] = ToStrArray(stdioArgs),
            ["activeSessions"] = _mcpSseClients.Count,
            ["workspaceRoot"] = GetEngine().Repository.WorkspaceRoot,
            ["focus"] = new JsonObject
            {
                ["modId"] = _studioFocus.ModId,
                ["modName"] = _studioFocus.ModName,
                ["category"] = _studioFocus.Category,
                ["itemKey"] = _studioFocus.ItemKey,
                ["itemName"] = _studioFocus.ItemName,
                ["scriptPath"] = _studioFocus.ScriptPath,
                ["updatedAtUtc"] = _studioFocus.UpdatedAtUtc.ToString("O")
            },
            ["tools"] = McpToolDispatcher.BuildToolDefinitions(),
            ["recentCalls"] = historyArr
        };
    }

    private async Task<JsonObject> HandleMcpUiCallAsync(JsonObject body, CancellationToken ct)
    {
        var toolName = Str(body, "tool");
        if (string.IsNullOrWhiteSpace(toolName)) throw new ModOperationException("Missing tool name.");
        var toolArgs = body["arguments"] as JsonObject ?? new JsonObject();
        var output = await McpToolDispatcher.ExecuteToolAsync(toolName, toolArgs, ct, BuildMcpHooks());
        return new JsonObject
        {
            ["ok"] = true,
            ["tool"] = toolName,
            ["output"] = output
        };
    }

    private JsonObject HandleMcpInstallConfig(JsonObject body)
    {
        var targetDir = NullIfBlank(Str(body, "targetDir")) ?? GetEngine().Repository.WorkspaceRoot;
        var transport = Str(body, "transport", "http").ToLowerInvariant();
        var configPath = Path.Combine(targetDir, ".mcp.json");

        JsonObject rootObj;
        if (File.Exists(configPath))
        {
            rootObj = JsonNode.Parse(File.ReadAllText(configPath, Encoding.UTF8)) as JsonObject ?? new JsonObject();
        }
        else
        {
            rootObj = new JsonObject();
        }

        var servers = rootObj["mcpServers"] as JsonObject ?? new JsonObject();
        rootObj["mcpServers"] = servers;

        var httpUrl = $"{BaseUrl.TrimEnd('/')}/mcp";
        if (transport == "stdio")
        {
            var exePath = Environment.ProcessPath ?? "YGORogueDuelistCardModInstaller.Desktop.exe";
            var mcpExeCandidate = Path.Combine(AppContext.BaseDirectory, "YGORogueDuelistCardModInstaller.Mcp.exe");
            servers["ygo-rogue-mod"] = File.Exists(mcpExeCandidate)
                ? new JsonObject { ["command"] = mcpExeCandidate, ["args"] = new JsonArray() }
                : new JsonObject { ["command"] = exePath, ["args"] = new JsonArray("--mcp") };
        }
        else
        {
            servers["ygo-rogue-mod"] = new JsonObject
            {
                ["type"] = "http",
                ["url"] = httpUrl
            };
        }

        File.WriteAllText(configPath, rootObj.ToJsonString(McpToolDispatcher.PrettyNodeOptions), ModPatcher.Utf8NoBom);
        EmitLog(LogLevel.Ok, $"已将内置 MCP 配置写入：{configPath}");
        return new JsonObject
        {
            ["ok"] = true,
            ["configPath"] = configPath,
            ["url"] = httpUrl
        };
    }

    private JsonObject HandleMcpClearLogs()
    {
        while (_mcpCallHistory.TryDequeue(out _)) { }
        return new JsonObject { ["ok"] = true };
    }

    private static JsonObject SerializeMcpCall(McpCallRecord r) => new()
    {
        ["id"] = r.Id,
        ["tool"] = r.ToolName,
        ["args"] = r.ArgsSummary,
        ["ok"] = r.Ok,
        ["durationMs"] = r.DurationMs,
        ["preview"] = r.ResultPreview,
        ["time"] = r.TimestampUtc.ToLocalTime().ToString("HH:mm:ss")
    };

    // ==================== BUILT-IN UPDATE CENTER, PROXY & VANILLA DLL INSPECTOR ====================

    private JsonObject HandleVanillaDllInfo()
    {
        var engine = GetEngine();
        var status = engine.GetStatus();
        var installDir = status.InstallDir;
        var dllPath = string.IsNullOrWhiteSpace(installDir) ? null : Path.Combine(installDir, GameLocator.GameDllName);
        var info = LauncherInspector.InspectDllDetailed(dllPath);
        if (info is null)
        {
            return new JsonObject
            {
                ["ok"] = true,
                ["found"] = false,
                ["expectedPath"] = dllPath ?? string.Empty
            };
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["found"] = true,
            ["fullPath"] = info.FullPath,
            ["fileSizeBytes"] = info.FileSizeBytes,
            ["lastWriteTimeUtc"] = info.LastWriteTimeUtc.ToString("O"),
            ["sha256"] = info.Sha256,
            ["assemblyName"] = info.AssemblyName,
            ["assemblyVersion"] = info.AssemblyVersion,
            ["launcherBuild"] = info.LauncherBuild,
            ["initialBundleVersion"] = info.InitialBundleVersion,
            ["initialBundleFileName"] = info.InitialBundleFileName,
            ["initialBundleSha256"] = info.InitialBundleSha256,
            ["hasStartupHashesResource"] = info.HasStartupHashesResource,
            ["startupHashesSizeBytes"] = info.StartupHashesSizeBytes,
            ["hasBackupOrig"] = info.HasBackupOrig,
            ["embeddedResources"] = ToStrArray(info.EmbeddedResources),
            ["updateEndpoints"] = ToStrArray(info.UpdateEndpoints),
            ["desktopBootScript"] = info.DesktopBootScript
        };
    }

    private async Task<JsonObject> HandleUpdatesCheckAsync(HttpListenerRequest req, CancellationToken ct)
    {
        var engine = GetEngine();
        var state = engine.Repository.LoadState();
        var status = engine.GetStatus();
        var proxyOverride = req.QueryString["proxy"];
        var proxy = proxyOverride is not null
            ? GitHubReleaseService.NormalizeProxyPrefix(proxyOverride)
            : GitHubReleaseService.NormalizeProxyPrefix(state.GitHubProxy);

        var gameRepo = string.IsNullOrWhiteSpace(state.Repo) ? InstallOptions.DefaultRepo : state.Repo;
        var mgrRepo = string.IsNullOrWhiteSpace(state.ManagerRepo) ? InstallOptions.DefaultManagerRepo : state.ManagerRepo;
        var silent = new SseInstallerInteraction(EmitLog, EmitProgress);

        var gameReleaseTask = GitHubReleaseService.GetLatestReleaseAsync(gameRepo, silent, ct, proxy);
        var mgrReleaseTask = GitHubReleaseService.GetLatestReleaseAsync(mgrRepo, silent, ct, proxy);
        await Task.WhenAll(gameReleaseTask, mgrReleaseTask);

        var gameRel = await gameReleaseTask;
        var mgrRel = await mgrReleaseTask;

        var localGameVer = status.ActiveBaseGameVersion ?? status.LauncherMeta?.InitialBundleVersion;
        var gameHasUpdate = gameRel?.EffectiveTag is { Length: > 0 } gTag &&
                            (string.IsNullOrWhiteSpace(localGameVer) || GameLocator.CompareVersionText(gTag, localGameVer) > 0);
        var mgrHasUpdate = mgrRel?.EffectiveTag is { Length: > 0 } mTag &&
                           GameLocator.CompareVersionText(mTag, ModManifest.CurrentManagerVersion) > 0;

        var downloadsDir = Path.Combine(engine.Repository.WorkspaceRoot, "downloads");
        var partialsArr = new JsonArray();
        foreach (var p in GitHubReleaseService.ListPartialDownloads(downloadsDir))
        {
            partialsArr.Add((JsonNode?)new JsonObject
            {
                ["fileName"] = p.FileName,
                ["downloadedBytes"] = p.DownloadedBytes,
                ["partPath"] = p.PartPath
            });
        }

        var presetsArr = new JsonArray();
        foreach (var pr in GitHubReleaseService.BuiltInProxyPresets)
        {
            presetsArr.Add((JsonNode?)new JsonObject
            {
                ["id"] = pr.Id,
                ["nameZh"] = pr.NameZh,
                ["nameEn"] = pr.NameEn,
                ["prefix"] = pr.Prefix
            });
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["proxy"] = proxy,
            ["proxyPresets"] = presetsArr,
            ["downloadsDir"] = downloadsDir,
            ["partialDownloads"] = partialsArr,
            ["game"] = new JsonObject
            {
                ["repo"] = gameRepo,
                ["localVersion"] = localGameVer,
                ["launcherBuild"] = status.LauncherMeta?.LauncherBuild ?? 0,
                ["latestTag"] = gameRel?.EffectiveTag,
                ["hasUpdate"] = gameHasUpdate,
                ["publishedAt"] = gameRel?.PublishedAt,
                ["htmlUrl"] = gameRel?.HtmlUrl ?? $"https://github.com/{gameRepo}/releases",
                ["hasWebBundle"] = gameRel is not null && GitHubReleaseService.FindWebBundleAsset(gameRel) is not null,
                ["webBundleAsset"] = SerializeAsset(gameRel is not null ? GitHubReleaseService.FindWebBundleAsset(gameRel) : null),
                ["installerAsset"] = SerializeAsset(gameRel is not null ? GitHubReleaseService.FindInstallerAsset(gameRel) : null)
            },
            ["manager"] = new JsonObject
            {
                ["repo"] = mgrRepo,
                ["localVersion"] = ModManifest.CurrentManagerVersion,
                ["latestTag"] = mgrRel?.EffectiveTag,
                ["hasUpdate"] = mgrHasUpdate,
                ["publishedAt"] = mgrRel?.PublishedAt,
                ["htmlUrl"] = mgrRel?.HtmlUrl ?? $"https://github.com/{mgrRepo}/releases",
                ["assets"] = new JsonArray((mgrRel?.Assets ?? []).Select(a => (JsonNode?)SerializeAsset(a)).ToArray())
            }
        };
    }

    private static JsonObject? SerializeAsset(GitHubAsset? asset) => asset is null ? null : new JsonObject
    {
        ["name"] = asset.Name,
        ["size"] = asset.Size,
        ["downloadUrl"] = asset.BrowserDownloadUrl,
        ["sha256"] = asset.ExpectedSha256
    };

    private async Task<JsonObject> HandleTestProxyAsync(JsonObject body, CancellationToken ct)
    {
        var proxy = Str(body, "proxy");
        var repo = NullIfBlank(Str(body, "repo")) ?? InstallOptions.DefaultRepo;
        var r = await GitHubReleaseService.TestProxyAsync(proxy, repo, ct);
        return new JsonObject
        {
            ["ok"] = r.Ok,
            ["statusCode"] = r.StatusCode,
            ["latencyMs"] = r.LatencyMs,
            ["effectiveUrl"] = r.EffectiveUrl,
            ["error"] = r.Error
        };
    }

    private async Task<JsonObject> HandleDownloadGameUpdateAsync(JsonObject body, CancellationToken ct)
    {
        if (_isBusy) throw new ModOperationException("已有下载或注入任务正在执行中。");
        _isBusy = true;
        using var dlCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _activeDownloadCts = dlCts;
        var mode = Str(body, "mode", "web-bundle").ToLowerInvariant();
        var force = Bool(body, "force", false);
        _activeDownloadLabel = mode == "installer" ? "原版完整安装包 (.exe)" : "原版轻量数据包 (web-.zip)";

        var interaction = new SseInstallerInteraction(EmitLog, EmitProgress);
        try
        {
            var engine = GetEngine();
            await engine.UpdateOfficialGameAsync(
                interaction,
                dlCts.Token,
                preferWebBundle: mode != "installer",
                forceDownload: force);
            InvalidateCaches();
            return new JsonObject { ["ok"] = true, ["mode"] = mode };
        }
        finally
        {
            _activeDownloadCts = null;
            _activeDownloadLabel = null;
            _isBusy = false;
        }
    }

    private async Task<JsonObject> HandleDownloadManagerUpdateAsync(JsonObject body, CancellationToken ct)
    {
        if (_isBusy) throw new ModOperationException("已有下载或注入任务正在执行中。");
        _isBusy = true;
        using var dlCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _activeDownloadCts = dlCts;
        _activeDownloadLabel = "MOD 管理器最新版";

        var interaction = new SseInstallerInteraction(EmitLog, EmitProgress);
        try
        {
            var engine = GetEngine();
            var state = engine.Repository.LoadState();
            var mgrRepo = string.IsNullOrWhiteSpace(state.ManagerRepo) ? InstallOptions.DefaultManagerRepo : state.ManagerRepo;
            var proxy = state.GitHubProxy;

            var release = await GitHubReleaseService.GetLatestReleaseAsync(mgrRepo, interaction, dlCts.Token, proxy)
                ?? throw new ModOperationException($"无法获取 {mgrRepo} 的最新版本，请检查网络或启用代理加速。");

            var requestedName = NullIfBlank(Str(body, "assetName"));
            var asset = (requestedName is not null
                    ? release.Assets.FirstOrDefault(a => string.Equals(a.Name, requestedName, StringComparison.OrdinalIgnoreCase))
                    : null)
                ?? release.Assets.FirstOrDefault(a => a.Name?.Contains("Desktop", StringComparison.OrdinalIgnoreCase) == true && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ?? release.Assets.FirstOrDefault(a => a.Name?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true)
                ?? release.Assets.FirstOrDefault()
                ?? throw new ModOperationException($"发布 {release.EffectiveTag} 中没有可下载的附件。");

            var downloadsDir = Path.Combine(engine.Repository.WorkspaceRoot, "downloads");
            var dest = Path.Combine(downloadsDir, asset.Name!);
            var savedPath = await GitHubReleaseService.DownloadResumableAsync(
                asset.BrowserDownloadUrl!,
                dest,
                asset.Size,
                asset.ExpectedSha256,
                proxy,
                interaction,
                dlCts.Token);

            return new JsonObject
            {
                ["ok"] = true,
                ["tag"] = release.EffectiveTag,
                ["savedPath"] = savedPath,
                ["downloadsDir"] = downloadsDir
            };
        }
        finally
        {
            _activeDownloadCts = null;
            _activeDownloadLabel = null;
            _isBusy = false;
        }
    }

    private JsonObject HandleCancelDownload()
    {
        var cts = _activeDownloadCts;
        if (cts is null)
        {
            return new JsonObject { ["ok"] = true, ["cancelled"] = false };
        }
        try { cts.Cancel(); } catch { }
        return new JsonObject { ["ok"] = true, ["cancelled"] = true };
    }
}
