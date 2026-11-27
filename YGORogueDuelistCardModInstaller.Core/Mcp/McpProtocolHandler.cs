using System.Text;
using System.Text.Json.Nodes;

namespace YGORogueDuelistCardModInstaller.Core.Mcp;

/// <summary>
/// Transport-agnostic JSON-RPC 2.0 Model Context Protocol (MCP) handler shared by:
/// 1. Built-in Streamable HTTP (/mcp) &amp; Legacy SSE (/mcp/sse) inside Desktop Studio
/// 2. Stdio transport (YGORogueDuelistCardModInstaller.Mcp.exe and Desktop.exe --mcp)
/// 100% Native AOT compatible.
/// </summary>
public static class McpProtocolHandler
{
    public const string ServerName = "ygo-rogue-duelist-mod-mcp";
    public const string ServerVersion = "2.0.0";
    public const string DefaultProtocolVersion = "2025-03-26";

    /// <summary>
    /// Processes a single JSON-RPC request or notification. Returns null for notifications that require no reply.
    /// </summary>
    public static async Task<JsonObject?> HandleMessageAsync(
        JsonNode? request,
        McpHostHooks? hooks,
        CancellationToken ct)
    {
        if (request is not JsonObject reqObj)
        {
            return BuildError(null, -32600, "Invalid Request");
        }

        var id = reqObj["id"]?.DeepClone();
        var method = reqObj["method"]?.GetValue<string>() ?? string.Empty;
        var @params = reqObj["params"] as JsonObject ?? new JsonObject();

        // JSON-RPC notifications have no id and require no response
        if (id is null && (method.StartsWith("notifications/", StringComparison.Ordinal) || method == "initialized"))
        {
            return null;
        }

        try
        {
            JsonNode result = method switch
            {
                "initialize" => HandleInitialize(@params),
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = McpToolDispatcher.BuildToolDefinitions() },
                "tools/call" => await HandleToolCallAsync(@params, hooks, ct),
                "resources/list" => HandleResourcesList(),
                "resources/read" => await HandleResourcesReadAsync(@params, hooks, ct),
                "prompts/list" => HandlePromptsList(),
                "prompts/get" => HandlePromptsGet(@params),
                _ => throw new KeyNotFoundException($"Method not found: {method}")
            };

            return id is not null ? BuildResult(id, result) : null;
        }
        catch (KeyNotFoundException ex)
        {
            return id is not null ? BuildError(id, -32601, ex.Message) : null;
        }
        catch (Exception ex)
        {
            return id is not null ? BuildError(id, -32000, ex.Message) : null;
        }
    }

    public static async Task<JsonNode?> HandleRawJsonAsync(string rawJson, McpHostHooks? hooks, CancellationToken ct)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(rawJson);
        }
        catch (Exception ex)
        {
            return BuildError(null, -32700, $"Parse error: {ex.Message}");
        }

        if (parsed is JsonArray batch)
        {
            var responses = new JsonArray();
            foreach (var item in batch)
            {
                var resp = await HandleMessageAsync(item, hooks, ct);
                if (resp is not null) responses.Add((JsonNode?)resp);
            }
            return responses.Count > 0 ? responses : null;
        }

        return await HandleMessageAsync(parsed, hooks, ct);
    }

    public static async Task RunStdioLoopAsync(McpHostHooks? hooks = null, CancellationToken ct = default)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        string? line;
        while (!ct.IsCancellationRequested && (line = await Console.In.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var reply = await HandleRawJsonAsync(line, hooks, ct);
            if (reply is not null)
            {
                Console.Out.WriteLine(reply.ToJsonString(ModPatcher.JsonOptions));
                Console.Out.Flush();
            }
        }
    }

    private static JsonObject HandleInitialize(JsonObject @params)
    {
        var requested = @params["protocolVersion"]?.GetValue<string>();
        var proto = requested is "2024-11-05" or "2025-03-26" or "2025-06-18"
            ? requested
            : DefaultProtocolVersion;

        return new JsonObject
        {
            ["protocolVersion"] = proto,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
                ["resources"] = new JsonObject { ["subscribe"] = false, ["listChanged"] = false },
                ["prompts"] = new JsonObject { ["listChanged"] = false }
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = ServerName,
                ["version"] = ServerVersion
            },
            ["instructions"] =
                "Built-in Yu-Gi-Oh! Rogue Duelist MOD Studio MCP Server. " +
                "Call get_studio_context to inspect the card/script currently open in the Studio, " +
                "get_vanilla_card_script to reference official ocgcore-wasm Lua scripts, " +
                "patch_mod_file / write_mod_file to edit Lua/JS/DSL/JSON files with automatic Studio refresh & Hot-Reload, " +
                "and validate_mod / trigger_hot_reload / apply_or_restore_mods to test live in-game."
        };
    }

    private static async Task<JsonObject> HandleToolCallAsync(JsonObject @params, McpHostHooks? hooks, CancellationToken ct)
    {
        var toolName = @params["name"]?.GetValue<string>()
            ?? throw new ModOperationException("Missing tool name.");
        var toolArgs = @params["arguments"] as JsonObject ?? new JsonObject();

        try
        {
            var output = await McpToolDispatcher.ExecuteToolAsync(toolName, toolArgs, ct, hooks);
            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = output }),
                ["isError"] = false
            };
        }
        catch (Exception ex)
        {
            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"Error: {ex.Message}" }),
                ["isError"] = true
            };
        }
    }

    private static JsonObject HandleResourcesList() => new()
    {
        ["resources"] = new JsonArray(
            Res("ygo://studio/context", "Live Desktop Studio Focus", "Currently selected MOD, card/relic/character, and open script content in Desktop Studio.", "application/json"),
            Res("ygo://docs/mod-spec", "Mod Spec v1 Overview", "Complete directory layout & architecture for Yu-Gi-Oh! Rogue Duelist MODs.", "text/markdown"),
            Res("ygo://docs/xmake-dsl", "Xmake-Style Lua/JS DSL Guide", "Dual-scope mod.lua and mod.js syntax for declarative targets + runtime hooks.", "text/markdown"),
            Res("ygo://docs/lua-ocgcore", "ocgcore-wasm Lua Scripting Guide", "Lua card scripting & utility.lua helper guide plus bitmask table.", "text/markdown"),
            Res("ygo://docs/js-api", "window.YgoModAPI Reference", "Frontend ES Module plugin API, fetch hooks, DOM observer, storage, and F8/F9/F10 overlays.", "text/markdown"),
            Res("ygo://docs/trainer-schema", "Trainer / Modifier Guide", "Declarative trainer.json + F8 in-game live editor schema.", "text/markdown"),
            Res("ygo://game/status", "Live Mod Manager Status", "Current game version, managed mods, sync state, and conflicts.", "application/json"))
    };

    private static async Task<JsonObject> HandleResourcesReadAsync(JsonObject @params, McpHostHooks? hooks, CancellationToken ct)
    {
        var uri = @params["uri"]?.GetValue<string>() ?? string.Empty;
        var (mime, text) = uri switch
        {
            "ygo://studio/context" => ("application/json", await McpToolDispatcher.ExecuteToolAsync("get_studio_context", new JsonObject(), ct, hooks)),
            "ygo://docs/mod-spec" => ("text/markdown", ModKnowledgeBase.OverviewGuide + "\n\n" + ModKnowledgeBase.CardGuide + "\n\n" + ModKnowledgeBase.CharacterGuide + "\n\n" + ModKnowledgeBase.RelicGuide + "\n\n" + ModKnowledgeBase.PackDeckGuide),
            "ygo://docs/xmake-dsl" => ("text/markdown", ModKnowledgeBase.DslGuide),
            "ygo://docs/lua-ocgcore" => ("text/markdown", ModKnowledgeBase.LuaGuide + "\n\n" + ModKnowledgeBase.BitmaskGuide),
            "ygo://docs/js-api" => ("text/markdown", ModKnowledgeBase.JsGuide),
            "ygo://docs/trainer-schema" => ("text/markdown", ModKnowledgeBase.TrainerGuide),
            "ygo://game/status" => ("application/json", await McpToolDispatcher.ExecuteToolAsync("get_manager_status", new JsonObject(), ct, hooks)),
            _ => throw new ModOperationException($"Unknown resource URI: {uri}")
        };

        return new JsonObject
        {
            ["contents"] = new JsonArray(
                new JsonObject { ["uri"] = uri, ["mimeType"] = mime, ["text"] = text })
        };
    }

    private static JsonObject HandlePromptsList() => new()
    {
        ["prompts"] = new JsonArray(
            PromptDef("edit-focused-script", "Read the script currently open in Desktop Studio via get_studio_context, apply the requested Lua/JS effect changes, validate & hot-reload.", "editGoal"),
            PromptDef("create-card-mod", "Guide AI step-by-step to design and create a new custom Yu-Gi-Oh! card + ocgcore Lua script.", "cardConcept"),
            PromptDef("create-character-mod", "Guide AI to design and add a custom playable duelist with signature deck & starting relic.", "characterConcept"),
            PromptDef("create-relic-mod", "Guide AI to design and add a custom relic with passive/duel Lua effect.", "relicConcept"),
            PromptDef("create-trainer-mod", "Guide AI to create a custom Trainer / Modifier MOD (unlock all, gold/LP, free shop, F8 panel).", "trainerFeatures"))
    };

    private static JsonObject HandlePromptsGet(JsonObject @params)
    {
        var name = @params["name"]?.GetValue<string>() ?? string.Empty;
        var pArgs = @params["arguments"] as JsonObject ?? new JsonObject();

        var promptText = name switch
        {
            "edit-focused-script" =>
                $"Edit the currently focused MOD script according to this goal: {pArgs["editGoal"]?.GetValue<string>() ?? "(ask user what effect they want)"}.\n" +
                "Steps:\n1. Call get_studio_context(includeScriptContent=true) to inspect the active MOD, item, and open script.\n" +
                "2. If writing ocgcore Lua, call get_vanilla_card_script for similar vanilla cards and get_mod_guide(topic='lua') if needed.\n" +
                "3. Apply clean edits using patch_mod_file or write_mod_file.\n" +
                "4. Call validate_mod and trigger_hot_reload so the user sees the change live immediately.",
            "create-card-mod" =>
                $"Design and build a Yu-Gi-Oh! Rogue Duelist custom card MOD for concept: {pArgs["cardConcept"]?.GetValue<string>() ?? "(ask user)"}.\n" +
                "Steps:\n1. Call get_mod_guide(topic='card'), get_mod_guide(topic='bitmasks'), and get_vanilla_card_script for reference.\n" +
                "2. Pick an unused custom ID in 190010000-199999999.\n" +
                "3. Call create_mod (if needed) and add_mod_card with complete English + Chinese text and a valid ocgcore-wasm Lua script.\n" +
                "4. Call validate_mod and trigger_hot_reload.",
            "create-character-mod" =>
                $"Design and build a playable character MOD for concept: {pArgs["characterConcept"]?.GetValue<string>() ?? "(ask user)"}.\n" +
                "Steps:\n1. Call get_mod_guide(topic='character') and search_game_data(kind='cards') / search_game_data(kind='relics').\n" +
                "2. Call create_mod and add_mod_character with unlockedByDefault=true.\n" +
                "3. Call validate_mod and trigger_hot_reload.",
            "create-relic-mod" =>
                $"Design and build a custom relic MOD for concept: {pArgs["relicConcept"]?.GetValue<string>() ?? "(ask user)"}.\n" +
                "Steps:\n1. Call get_mod_guide(topic='relic') and get_mod_guide(topic='lua').\n" +
                "2. Call create_mod and add_mod_relic with a clean GlobalEffect Lua script.\n" +
                "3. Call validate_mod and trigger_hot_reload.",
            "create-trainer-mod" =>
                $"Create a Trainer/Modifier MOD with features: {pArgs["trainerFeatures"]?.GetValue<string>() ?? "unlock all, extra gold, F8 panel"}.\n" +
                "Steps:\n1. Call get_mod_guide(topic='trainer').\n" +
                "2. Call create_mod and configure_mod_trainer.\n" +
                "3. Call validate_mod and trigger_hot_reload.",
            _ => throw new ModOperationException($"Unknown prompt: {name}")
        };

        return new JsonObject
        {
            ["description"] = $"Guided workflow: {name}",
            ["messages"] = new JsonArray(
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonObject { ["type"] = "text", ["text"] = promptText }
                })
        };
    }

    private static JsonObject Res(string uri, string name, string desc, string mime) => new()
    {
        ["uri"] = uri,
        ["name"] = name,
        ["description"] = desc,
        ["mimeType"] = mime
    };

    private static JsonObject PromptDef(string name, string desc, string argName) => new()
    {
        ["name"] = name,
        ["description"] = desc,
        ["arguments"] = new JsonArray(
            new JsonObject { ["name"] = argName, ["description"] = "User idea or edit goal", ["required"] = false })
    };

    public static JsonObject BuildResult(JsonNode id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result
    };

    public static JsonObject BuildError(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message
        }
    };
}
