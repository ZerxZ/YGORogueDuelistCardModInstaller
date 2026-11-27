using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace YGORogueDuelistCardModInstaller.Core.ModManager;

public sealed record ModOrganizeItem(
    string ModId,
    string ModName,
    string OldCategory,
    string NewCategory,
    int OldOrder,
    int NewOrder,
    string Reason);

public sealed record ModOrganizePlan(
    string Mode,
    string Summary,
    List<ModOrganizeItem> Items,
    List<string> SuggestedCustomCategories);

/// <summary>
/// Intelligent MOD Checklist Organizer (100% Native AOT compatible):
///   1) Local Smart Rule Engine: classifies uncategorized/misclassified MODs from stats, tags, descriptions
///      and computes optimal conflict-safe load order (Overhaul -> Cards -> Packs/Decks -> Characters -> Relics -> Scripts -> UI -> Trainer).
///   2) Optional OpenAI-Compatible LLM Organizer: sends compact MOD catalog metadata to any user-configured LLM API
///      (OpenAI, DeepSeek, Ollama, OpenRouter, etc.) for custom thematic categorization & reasoning!
/// </summary>
public static class ModOrganizer
{
    private static readonly string[] LoadLayerOrder =
    [
        "overhaul",
        "cards",
        "packs_decks",
        "characters",
        "relics",
        "script_plugin",
        "ui_theme",
        "trainer",
        "other"
    ];

    public static ModOrganizePlan BuildLocalSmartPlan(
        IReadOnlyList<ManagedMod> mods,
        IReadOnlyList<ModConflict> conflicts,
        bool preserveExistingCustomCategories = true)
    {
        var conflictMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in conflicts)
        {
            foreach (var id in c.ModIds)
            {
                conflictMap[id] = conflictMap.GetValueOrDefault(id) + 1;
            }
        }

        var proposed = new List<(ManagedMod Mod, string OldCat, string NewCat, int Layer, string Reason)>();

        foreach (var m in mods)
        {
            var oldCat = ModRepository.ResolveEffectiveCategory(m.Manifest, m.Stats);
            var isExplicitCustom = !string.IsNullOrWhiteSpace(m.Manifest.Category) &&
                                   !ModRepository.StandardCategories.Contains(m.Manifest.Category.Trim(), StringComparer.OrdinalIgnoreCase);

            string newCat;
            string reason;
            if (preserveExistingCustomCategories && isExplicitCustom)
            {
                newCat = m.Manifest.Category!.Trim();
                reason = $"保留玩家自定义分类「{newCat}」";
            }
            else
            {
                (newCat, reason) = ClassifyModSmart(m);
            }

            var layer = ResolveLoadLayer(newCat, m.Stats);
            if (conflictMap.TryGetValue(m.Id, out var cCount) && cCount > 0)
            {
                reason += $"（涉及 {cCount} 处重叠，按层级排序）";
            }

            proposed.Add((m, oldCat, newCat, layer, reason));
        }

        // Optimal load order: Enabled first -> Layer priority -> Larger content packs earlier -> Alphabetical
        proposed.Sort((a, b) =>
        {
            if (a.Mod.Enabled != b.Mod.Enabled) return a.Mod.Enabled ? -1 : 1;
            var cmp = a.Layer.CompareTo(b.Layer);
            if (cmp != 0) return cmp;
            var weightA = a.Mod.Stats.CardCount + a.Mod.Stats.CampaignFileCount * 10 + a.Mod.Stats.OverrideFileCount;
            var weightB = b.Mod.Stats.CardCount + b.Mod.Stats.CampaignFileCount * 10 + b.Mod.Stats.OverrideFileCount;
            if (weightA != weightB) return weightB.CompareTo(weightA);
            return StringComparer.OrdinalIgnoreCase.Compare(a.Mod.Manifest.Name, b.Mod.Manifest.Name);
        });

        var items = new List<ModOrganizeItem>(proposed.Count);
        var changedCats = 0;
        var changedOrders = 0;
        for (var i = 0; i < proposed.Count; i++)
        {
            var p = proposed[i];
            if (!string.Equals(p.OldCat, p.NewCat, StringComparison.OrdinalIgnoreCase)) changedCats++;
            if (p.Mod.Order != i) changedOrders++;
            items.Add(new ModOrganizeItem(
                p.Mod.Id,
                p.Mod.Manifest.Name,
                p.OldCat,
                p.NewCat,
                p.Mod.Order,
                i,
                p.Reason));
        }

        var summary = $"本地智能整理完成：共分析 {mods.Count} 个 MOD，更新分类 {changedCats} 项、优化加载顺序 {changedOrders} 项" +
                      "（顺序：大型规则/综合 → 卡牌 → 卡包/卡组 → 角色 → 遗物 → 脚本 → 界面美化 → 修改器）。";

        return new ModOrganizePlan("smart", summary, items, []);
    }

    public static async Task<ModOrganizePlan> BuildAiPlanAsync(
        IReadOnlyList<ManagedMod> mods,
        IReadOnlyList<ModConflict> conflicts,
        string apiBaseUrl,
        string apiKey,
        string model,
        string? userPrompt,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiBaseUrl) || string.IsNullOrWhiteSpace(model))
        {
            throw new ModOperationException("请先填写 AI 接口地址 (Base URL) 与模型名称（支持 OpenAI / DeepSeek / Ollama / 任意 OpenAI 兼容 API）。");
        }

        var endpoint = apiBaseUrl.Trim().TrimEnd('/');
        if (!endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            endpoint += endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? "/chat/completions"
                : "/v1/chat/completions";
        }

        var catalogArr = new JsonArray();
        foreach (var m in mods)
        {
            catalogArr.Add((JsonNode?)new JsonObject
            {
                ["id"] = m.Id,
                ["name"] = m.Manifest.Name,
                ["author"] = m.Manifest.Author,
                ["enabled"] = m.Enabled,
                ["currentOrder"] = m.Order,
                ["currentCategory"] = ModRepository.ResolveEffectiveCategory(m.Manifest, m.Stats),
                ["description"] = m.Manifest.Description.Length > 260 ? m.Manifest.Description[..260] : m.Manifest.Description,
                ["tags"] = new JsonArray(m.Manifest.Tags.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
                ["stats"] = new JsonObject
                {
                    ["cards"] = m.Stats.CardCount,
                    ["characters"] = m.Stats.CharacterCount,
                    ["relics"] = m.Stats.RelicCount,
                    ["packs"] = m.Stats.PackCount,
                    ["decks"] = m.Stats.PreconstructedDeckCount + m.Stats.DeckCount,
                    ["lua"] = m.Stats.ScriptCount + m.Stats.LuaLibCount,
                    ["js"] = m.Stats.JsPluginCount,
                    ["css"] = m.Stats.CssStyleCount,
                    ["trainer"] = m.Stats.HasTrainer,
                    ["campaign"] = m.Stats.CampaignFileCount
                }
            });
        }

        var sysPrompt =
            "You are a Yu-Gi-Oh! Rogue Duelist MOD checklist organizer. " +
            "Given a list of MODs, organize them into clean categories and optimal load order " +
            "(earlier = base layer like overhauls/cards; later = higher priority like scripts/UI/trainers). " +
            "Standard categories: overhaul, cards, packs_decks, characters, relics, script_plugin, ui_theme, trainer, other. " +
            "You MAY also invent concise Chinese or English custom category names when the user asks or when grouping by archetype/theme makes sense. " +
            "Reply ONLY with valid JSON matching: " +
            "{\"summary\":\"short explanation\",\"items\":[{\"id\":\"exact mod id\",\"category\":\"category\",\"reason\":\"short reason\"}]} " +
            "in the exact desired load order (index 0 first), including EVERY input mod id once.";

        var userMsg = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(userPrompt))
        {
            userMsg.Append("用户整理要求：").AppendLine(userPrompt.Trim());
        }
        else
        {
            userMsg.AppendLine("请帮我将以下 MOD 列表清晰分类并排好合理的加载顺序（基础大包与卡牌在前，脚本美化与修改器在后）。");
        }
        userMsg.AppendLine("MOD 列表 JSON：");
        userMsg.AppendLine(catalogArr.ToJsonString());

        var reqBody = new JsonObject
        {
            ["model"] = model.Trim(),
            ["temperature"] = 0.2,
            ["messages"] = new JsonArray(
                (JsonNode?)new JsonObject { ["role"] = "system", ["content"] = sysPrompt },
                (JsonNode?)new JsonObject { ["role"] = "user", ["content"] = userMsg.ToString() })
        };

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }
        req.Content = new StringContent(reqBody.ToJsonString(), Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req, ct);
        var rawResp = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new ModOperationException($"AI 接口请求失败 (HTTP {(int)resp.StatusCode})：{rawResp[..Math.Min(260, rawResp.Length)]}");
        }

        var root = JsonNode.Parse(rawResp)?.AsObject()
            ?? throw new ModOperationException("AI 返回格式无效。");
        var content = root["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
            ?? throw new ModOperationException("AI 未返回 choices[0].message.content。");

        var jsonText = ExtractJsonBlock(content);
        var parsed = JsonNode.Parse(jsonText)?.AsObject()
            ?? throw new ModOperationException("AI 返回的 JSON 无法解析。");

        var byId = mods.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<ModOrganizeItem>();
        var customCats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (parsed["items"] is JsonArray aiItems)
        {
            foreach (var node in aiItems)
            {
                if (node is not JsonObject obj) continue;
                var id = obj["id"]?.GetValue<string>()?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(id) || !byId.TryGetValue(id, out var mod) || !seen.Add(id)) continue;

                var oldCat = ModRepository.ResolveEffectiveCategory(mod.Manifest, mod.Stats);
                var rawCat = obj["category"]?.GetValue<string>()?.Trim();
                var newCat = ModRepository.NormalizeCategory(rawCat) ?? oldCat;
                if (!ModRepository.StandardCategories.Contains(newCat, StringComparer.OrdinalIgnoreCase))
                {
                    customCats.Add(newCat);
                }

                var reason = obj["reason"]?.GetValue<string>()?.Trim() ?? "AI 推荐分类与顺序";
                items.Add(new ModOrganizeItem(mod.Id, mod.Manifest.Name, oldCat, newCat, mod.Order, items.Count, reason));
            }
        }

        // Append any MODs omitted by the LLM so nothing is ever lost
        foreach (var mod in mods)
        {
            if (seen.Contains(mod.Id)) continue;
            var oldCat = ModRepository.ResolveEffectiveCategory(mod.Manifest, mod.Stats);
            items.Add(new ModOrganizeItem(mod.Id, mod.Manifest.Name, oldCat, oldCat, mod.Order, items.Count, "保持原位"));
        }

        var summary = parsed["summary"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = $"AI ({model.Trim()}) 已为 {items.Count} 个 MOD 生成分类与加载顺序方案。";
        }

        return new ModOrganizePlan("ai", summary, items, customCats.ToList());
    }

    public static void ApplyPlan(ModRepository repo, ModOrganizePlan plan)
    {
        var state = repo.LoadState();
        var mods = repo.LoadMods(state);
        var byId = mods.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);

        // Register any new custom categories in state
        foreach (var item in plan.Items)
        {
            var norm = ModRepository.NormalizeCategory(item.NewCategory);
            if (!string.IsNullOrWhiteSpace(norm) &&
                !ModRepository.StandardCategories.Contains(norm, StringComparer.OrdinalIgnoreCase) &&
                !state.CustomCategories.Contains(norm, StringComparer.OrdinalIgnoreCase))
            {
                state.CustomCategories.Add(norm);
            }
        }
        repo.SaveState(state);

        // Update each MOD's category manifest
        foreach (var item in plan.Items)
        {
            if (!byId.TryGetValue(item.ModId, out var mod)) continue;
            var cur = ModRepository.ResolveEffectiveCategory(mod.Manifest, mod.Stats);
            if (!string.Equals(cur, item.NewCategory, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(mod.Manifest.Category))
            {
                repo.UpdateModMetadata(mod.Id, category: item.NewCategory);
            }
        }

        // Reorder according to plan
        state = repo.LoadState();
        mods = repo.LoadMods(state);
        byId = mods.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<ManagedMod>();
        foreach (var item in plan.Items.OrderBy(i => i.NewOrder))
        {
            if (byId.Remove(item.ModId, out var m)) ordered.Add(m);
        }
        ordered.AddRange(byId.Values);
        for (var i = 0; i < ordered.Count; i++) ordered[i].Order = i;
        repo.SaveModOrderAndFlags(ordered, state);
    }

    private static (string Category, string Reason) ClassifyModSmart(ManagedMod mod)
    {
        var s = mod.Stats;
        var text = $"{mod.Id} {mod.Manifest.Name} {mod.Manifest.Description} {string.Join(' ', mod.Manifest.Tags)}".ToLowerInvariant();

        var domains = (s.CardCount > 0 ? 1 : 0)
                    + (s.CharacterCount > 0 ? 1 : 0)
                    + (s.RelicCount > 0 ? 1 : 0)
                    + (s.PackCount + s.PreconstructedDeckCount + s.DeckCount > 0 ? 1 : 0);

        if (s.CampaignFileCount > 0 || (domains >= 3) || ContainsAny(text, "overhaul", "整合", "大修", "重制", "示例", "showcase"))
        {
            return ("overhaul", "包含多模块或战役规则改动 → 归入「大型综合 / 规则」底层");
        }
        if (s.HasTrainer && s.CardCount == 0 && s.CharacterCount == 0 && s.RelicCount == 0 ||
            ContainsAny(text, "trainer", "cheat", "修改器", "作弊", "无限金币"))
        {
            return ("trainer", "检测到修改器 / 数值定制 → 归入「修改器」并置于最高优先级");
        }
        if (s.CardCount > 0 || s.ScriptCount > 0)
        {
            if (s.CardCount == 0 && (s.PackCount > 0 || s.PreconstructedDeckCount > 0 || s.DeckCount > 0))
                return ("packs_decks", "以卡包/预组卡组为主 → 归入「卡包与预组」");
            return ("cards", $"包含 {s.CardCount} 张卡牌 / {s.ScriptCount} 个 Lua 脚本 → 归入「卡牌扩展」");
        }
        if (s.CharacterCount > 0 || ContainsAny(text, "character", "duelist", "角色", "决斗者"))
        {
            return ("characters", $"包含 {s.CharacterCount} 名角色 → 归入「角色扩展」");
        }
        if (s.RelicCount > 0 || ContainsAny(text, "relic", "artifact", "遗物"))
        {
            return ("relics", $"包含 {s.RelicCount} 个遗物 → 归入「遗物扩展」");
        }
        if (s.PackCount > 0 || s.PreconstructedDeckCount > 0 || s.DeckCount > 0 || ContainsAny(text, "deck", "pack", "卡组", "卡包"))
        {
            return ("packs_decks", "包含卡包或预组卡组 → 归入「卡包与预组」");
        }
        if (s.CssStyleCount > 0 && s.JsPluginCount == 0 || ContainsAny(text, "theme", "ui", "skin", "美化", "主题", "立绘", "皮肤"))
        {
            return ("ui_theme", "包含样式 / 界面美化资源 → 归入「界面与美化」");
        }
        if (s.JsPluginCount > 0 || s.LuaLibCount > 0 || s.HasJsDsl || s.HasLuaDsl)
        {
            return ("script_plugin", "包含 JS / Lua 插件脚本 → 归入「脚本与插件」");
        }
        return ("other", "通用扩展资源 → 归入「其他」");
    }

    private static int ResolveLoadLayer(string category, ModContentStats stats)
    {
        var idx = Array.FindIndex(LoadLayerOrder, c => c.Equals(category, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) return idx;
        // Custom categories: place intelligently based on content
        if (stats.HasTrainer && stats.CardCount == 0) return 7;
        if (stats.CampaignFileCount > 0) return 0;
        if (stats.CardCount > 0) return 1;
        return 4;
    }

    private static bool ContainsAny(string haystack, params string[] needles) =>
        needles.Any(n => haystack.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static string ExtractJsonBlock(string text)
    {
        var trimmed = text.Trim();
        var fenceStart = trimmed.IndexOf("```", StringComparison.Ordinal);
        if (fenceStart >= 0)
        {
            var nl = trimmed.IndexOf('\n', fenceStart);
            var fenceEnd = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (nl >= 0 && fenceEnd > nl)
            {
                trimmed = trimmed[(nl + 1)..fenceEnd].Trim();
            }
        }
        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');
        return firstBrace >= 0 && lastBrace > firstBrace ? trimmed[firstBrace..(lastBrace + 1)] : trimmed;
    }
}
