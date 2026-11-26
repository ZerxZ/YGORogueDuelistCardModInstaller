using System.Text;
using System.Text.Json.Nodes;
using YGORogueDuelistCardModInstaller.Core;
using YGORogueDuelistCardModInstaller.Mcp;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

const string ServerName = "ygo-rogue-duelist-mod-mcp";
const string ServerVersion = "1.0.0";
const string ProtocolVersion = "2024-11-05";

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

string? line;
while (!cts.IsCancellationRequested && (line = await Console.In.ReadLineAsync(cts.Token)) is not null)
{
    if (string.IsNullOrWhiteSpace(line)) continue;

    JsonNode? request;
    try
    {
        request = JsonNode.Parse(line);
    }
    catch (Exception ex)
    {
        WriteJsonRpcError(null, -32700, $"Parse error: {ex.Message}");
        continue;
    }

    if (request is not JsonObject reqObj)
    {
        WriteJsonRpcError(null, -32600, "Invalid Request");
        continue;
    }

    var id = reqObj["id"]?.DeepClone();
    var method = reqObj["method"]?.GetValue<string>() ?? string.Empty;
    var @params = reqObj["params"] as JsonObject ?? new JsonObject();

    // JSON-RPC notifications have no id and require no response
    if (id is null && method.StartsWith("notifications/", StringComparison.Ordinal))
    {
        continue;
    }

    try
    {
        JsonNode result = method switch
        {
            "initialize" => HandleInitialize(),
            "ping" => new JsonObject(),
            "tools/list" => new JsonObject { ["tools"] = McpToolDispatcher.BuildToolDefinitions() },
            "tools/call" => await HandleToolCallAsync(@params, cts.Token),
            "resources/list" => HandleResourcesList(),
            "resources/read" => HandleResourcesRead(@params),
            "prompts/list" => HandlePromptsList(),
            "prompts/get" => HandlePromptsGet(@params),
            _ => throw new KeyNotFoundException($"Method not found: {method}")
        };

        if (id is not null)
        {
            WriteJsonRpcResult(id, result);
        }
    }
    catch (KeyNotFoundException ex)
    {
        if (id is not null) WriteJsonRpcError(id, -32601, ex.Message);
    }
    catch (Exception ex)
    {
        if (id is not null) WriteJsonRpcError(id, -32000, ex.Message);
    }
}

static JsonObject HandleInitialize() => new()
{
    ["protocolVersion"] = ProtocolVersion,
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
        "Use this MCP server to inspect Yu-Gi-Oh! Rogue Duelist game data, read Mod Spec v1 guides, " +
        "and create/edit/validate/inject MODs (new cards + Lua scripts, playable characters, relics, JS plugins, and trainers) " +
        "using the zero-touch VirtualBundle injection engine."
};

static async Task<JsonObject> HandleToolCallAsync(JsonObject @params, CancellationToken ct)
{
    var toolName = @params["name"]?.GetValue<string>()
        ?? throw new ModOperationException("Missing tool name.");
    var toolArgs = @params["arguments"] as JsonObject ?? new JsonObject();

    try
    {
        var output = await McpToolDispatcher.ExecuteToolAsync(toolName, toolArgs, ct);
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

static JsonObject HandleResourcesList() => new()
{
    ["resources"] = new JsonArray(
        Res("ygo://docs/mod-spec", "Mod Spec v1 Overview", "Complete directory layout & architecture for Yu-Gi-Oh! Rogue Duelist MODs.", "text/markdown"),
        Res("ygo://docs/xmake-dsl", "Xmake-Style Lua/JS DSL Guide", "Dual-scope mod.lua and mod.js syntax for declarative targets + runtime hooks.", "text/markdown"),
        Res("ygo://docs/lua-ocgcore", "ocgcore-wasm Lua Scripting Guide", "Lua card scripting & utility.lua helper guide plus bitmask table.", "text/markdown"),
        Res("ygo://docs/js-api", "window.YgoModAPI Reference", "Frontend ES Module plugin API, fetch hooks, DOM observer, storage, and F9/F10 overlay.", "text/markdown"),
        Res("ygo://docs/trainer-schema", "Trainer / Modifier Guide", "Declarative trainer.json + F8 in-game live editor schema.", "text/markdown"),
        Res("ygo://game/status", "Live Mod Manager Status", "Current game version, managed mods, sync state, and conflicts.", "application/json"))
};

static JsonObject HandleResourcesRead(JsonObject @params)
{
    var uri = @params["uri"]?.GetValue<string>() ?? string.Empty;
    var (mime, text) = uri switch
    {
        "ygo://docs/mod-spec" => ("text/markdown", ModKnowledgeBase.OverviewGuide + "\n\n" + ModKnowledgeBase.CardGuide + "\n\n" + ModKnowledgeBase.CharacterGuide + "\n\n" + ModKnowledgeBase.RelicGuide + "\n\n" + ModKnowledgeBase.PackDeckGuide),
        "ygo://docs/xmake-dsl" => ("text/markdown", ModKnowledgeBase.DslGuide),
        "ygo://docs/lua-ocgcore" => ("text/markdown", ModKnowledgeBase.LuaGuide + "\n\n" + ModKnowledgeBase.BitmaskGuide),
        "ygo://docs/js-api" => ("text/markdown", ModKnowledgeBase.JsGuide),
        "ygo://docs/trainer-schema" => ("text/markdown", ModKnowledgeBase.TrainerGuide),
        "ygo://game/status" => ("application/json", McpToolDispatcher.ExecuteToolAsync("get_manager_status", new JsonObject(), CancellationToken.None).GetAwaiter().GetResult()),
        _ => throw new ModOperationException($"Unknown resource URI: {uri}")
    };

    return new JsonObject
    {
        ["contents"] = new JsonArray(
            new JsonObject { ["uri"] = uri, ["mimeType"] = mime, ["text"] = text })
    };
}

static JsonObject HandlePromptsList() => new()
{
    ["prompts"] = new JsonArray(
        PromptDef("create-card-mod", "Guide AI step-by-step to design and create a new custom Yu-Gi-Oh! card + ocgcore Lua script.", "cardConcept"),
        PromptDef("create-character-mod", "Guide AI to design and add a custom playable duelist with signature deck & starting relic.", "characterConcept"),
        PromptDef("create-relic-mod", "Guide AI to design and add a custom relic with passive/duel Lua effect.", "relicConcept"),
        PromptDef("create-trainer-mod", "Guide AI to create a custom Trainer / Modifier MOD (unlock all, gold/LP, free shop, F8 panel).", "trainerFeatures"))
};

static JsonObject HandlePromptsGet(JsonObject @params)
{
    var name = @params["name"]?.GetValue<string>() ?? string.Empty;
    var pArgs = @params["arguments"] as JsonObject ?? new JsonObject();

    var promptText = name switch
    {
        "create-card-mod" =>
            $"Design and build a Yu-Gi-Oh! Rogue Duelist custom card MOD for concept: {pArgs["cardConcept"]?.GetValue<string>() ?? "(ask user)"}.\n" +
            "Steps:\n1. Call get_mod_guide(topic='card') and get_mod_guide(topic='bitmasks') and get_mod_guide(topic='lua').\n" +
            "2. Pick an unused custom ID in 190010000-199999999.\n" +
            "3. Call create_mod (if needed) and add_mod_card with complete English + Chinese text and a valid ocgcore-wasm Lua script.\n" +
            "4. Call validate_mod and optionally apply_or_restore_mods(action='apply').",
        "create-character-mod" =>
            $"Design and build a playable character MOD for concept: {pArgs["characterConcept"]?.GetValue<string>() ?? "(ask user)"}.\n" +
            "Steps:\n1. Call get_mod_guide(topic='character') and search_game_data(kind='cards') / search_game_data(kind='relics') to verify real card names and relic keys.\n" +
            "2. Call create_mod and add_mod_character with unlockedByDefault=true.\n" +
            "3. Call validate_mod and apply_or_restore_mods(action='apply').",
        "create-relic-mod" =>
            $"Design and build a custom relic MOD for concept: {pArgs["relicConcept"]?.GetValue<string>() ?? "(ask user)"}.\n" +
            "Steps:\n1. Call get_mod_guide(topic='relic') and get_mod_guide(topic='lua').\n" +
            "2. Call create_mod and add_mod_relic with a clean GlobalEffect Lua script.\n" +
            "3. Call validate_mod and apply_or_restore_mods(action='apply').",
        "create-trainer-mod" =>
            $"Create a Trainer/Modifier MOD with features: {pArgs["trainerFeatures"]?.GetValue<string>() ?? "unlock all, extra gold, F8 panel"}.\n" +
            "Steps:\n1. Call get_mod_guide(topic='trainer').\n" +
            "2. Call create_mod and configure_mod_trainer.\n" +
            "3. Call validate_mod and apply_or_restore_mods(action='apply').",
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

static JsonObject Res(string uri, string name, string desc, string mime) => new()
{
    ["uri"] = uri,
    ["name"] = name,
    ["description"] = desc,
    ["mimeType"] = mime
};

static JsonObject PromptDef(string name, string desc, string argName) => new()
{
    ["name"] = name,
    ["description"] = desc,
    ["arguments"] = new JsonArray(
        new JsonObject { ["name"] = argName, ["description"] = "User idea or feature description", ["required"] = false })
};

static void WriteJsonRpcResult(JsonNode id, JsonNode result)
{
    var envelope = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result
    };
    Console.Out.WriteLine(envelope.ToJsonString(ModPatcher.JsonOptions));
    Console.Out.Flush();
}

static void WriteJsonRpcError(JsonNode? id, int code, string message)
{
    var envelope = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message
        }
    };
    Console.Out.WriteLine(envelope.ToJsonString(ModPatcher.JsonOptions));
    Console.Out.Flush();
}
