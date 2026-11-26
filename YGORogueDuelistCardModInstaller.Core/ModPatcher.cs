using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace YGORogueDuelistCardModInstaller.Core;

public static class ModPatcher
{
    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public static readonly string[] PatchedAssetNames =
    [
        "campaign-files.json",
        "card-assets.js",
        "content.json",
        "difficulty-preference.js",
        "engine-data.json",
        "i18n/zh.json",
        "scripts-0.json",
        "ui-art.js"
    ];

    public static string ComputeSha256Hex(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static byte[] GetUtf8Bytes(string text) => Utf8NoBom.GetBytes(text);

    public static JsonNode ParseJson(string text) =>
        JsonNode.Parse(text) ?? throw new ModOperationException("JSON 解析结果为空。");

    public static string ToJsonText(JsonNode node) => node.ToJsonString(JsonOptions);

    public static string ReadZipText(ZipArchive zip, string entryName)
    {
        var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, entryName, StringComparison.Ordinal))
            ?? throw new ModOperationException($"游戏包里找不到 {entryName}");

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static Dictionary<string, byte[]> BuildPatchedFiles(
        string zipPath,
        JsonObject mod,
        string rootDir,
        string? picsDir,
        IInstallerInteraction interaction)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var zip = ZipFile.OpenRead(zipPath);

        interaction.Say($"读取原版数据：{zipPath}");

        interaction.ReportProgress("正在修补 content.json...", 15);
        var content = ParseJson(ReadZipText(zip, "content.json")).AsObject();
        PatchContent(content, mod, interaction);
        files["content.json"] = GetUtf8Bytes(ToJsonText(content));

        interaction.ReportProgress("正在修补 engine-data.json...", 28);
        var engine = ParseJson(ReadZipText(zip, "engine-data.json")).AsObject();
        PatchEngine(engine, mod, interaction);
        files["engine-data.json"] = GetUtf8Bytes(ToJsonText(engine));

        interaction.ReportProgress("正在修补 scripts-0.json...", 42);
        var scripts = ParseJson(ReadZipText(zip, "scripts-0.json")).AsObject();
        PatchScripts(scripts, mod, interaction);
        files["scripts-0.json"] = GetUtf8Bytes(ToJsonText(scripts));

        interaction.ReportProgress("正在修补 campaign-files.json...", 56);
        var campaign = ParseJson(ReadZipText(zip, "campaign-files.json")).AsObject();
        PatchCampaign(campaign, mod, interaction);
        files["campaign-files.json"] = GetUtf8Bytes(ToJsonText(campaign));

        interaction.ReportProgress("正在内联卡图到 card-assets.js...", 70);
        var assetsText = ReadZipText(zip, "card-assets.js");
        var effectivePics = string.IsNullOrWhiteSpace(picsDir)
            ? Path.Combine(Path.GetTempPath(), "no-pics")
            : picsDir;
        files["card-assets.js"] = GetUtf8Bytes(PatchCardAssets(assetsText, mod, rootDir, effectivePics, interaction));

        interaction.ReportProgress("正在修补中文翻译与脚本...", 82);
        files["i18n/zh.json"] = GetUtf8Bytes(PatchZh(ReadZipText(zip, "i18n/zh.json"), mod, interaction));
        files["difficulty-preference.js"] = GetUtf8Bytes(PatchDifficulty(ReadZipText(zip, "difficulty-preference.js")));
        files["ui-art.js"] = GetUtf8Bytes(PatchUiArt(ReadZipText(zip, "ui-art.js")));

        interaction.ReportProgress("正在更新 sw.js 哈希清单...", 90);
        var swText = ReadZipText(zip, "sw.js");
        files["sw.js"] = GetUtf8Bytes(PatchServiceWorker(swText, files, interaction));

        return files;
    }

    public static void PatchContent(JsonObject content, JsonObject mod, IInstallerInteraction interaction)
    {
        var cards = content["cards"]?.AsArray()
            ?? throw new ModOperationException("content.json 缺少 cards 字段");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in cards)
        {
            if (card is JsonObject cardObj)
            {
                seen.Add(NodeAsKeyString(cardObj["id"]));
            }
        }

        var added = 0;
        var decks = mod["decks"]?.AsObject()
            ?? throw new ModOperationException("mod.json 缺少 decks 字段");
        var deckCount = decks.Count;

        if (mod["cards"] is JsonArray modCards)
        {
            foreach (var card in modCards)
            {
                if (card is not JsonObject cardObj) continue;
                var id = NodeAsKeyString(cardObj["id"]);
                if (seen.Add(id))
                {
                    cards.Add(cardObj.DeepClone());
                    added++;
                }
            }
        }

        // The web UI looks these entries up by name (content.cards.find(x=>x.name===n)),
        // so characters[].cards has to stay an array of card NAMES, not ids.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in cards)
        {
            if (card is JsonObject cardObj)
            {
                names.Add(NodeAsKeyString(cardObj["name"]));
            }
        }

        // Some builds ship a smaller pool; fall back to the bundled copy of stock cards.
        var fbByName = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        if (mod["fallback"] is JsonArray fallbackCards)
        {
            foreach (var fb in fallbackCards)
            {
                if (fb is JsonObject fbObj)
                {
                    fbByName[NodeAsKeyString(fbObj["name"])] = fbObj;
                }
            }
        }

        var rdRecovered = 0;
        var dropped = new List<string>();

        JsonArray ResolveNames(JsonArray list)
        {
            var result = new JsonArray();
            foreach (var item in list)
            {
                var name = NodeAsKeyString(item);
                if (!names.Contains(name))
                {
                    if (fbByName.TryGetValue(name, out var fb))
                    {
                        var fbId = NodeAsKeyString(fb["id"]);
                        if (seen.Add(fbId))
                        {
                            cards.Add(fb.DeepClone());
                            rdRecovered++;
                        }

                        names.Add(name);
                    }
                    else
                    {
                        dropped.Add(name);
                        continue;
                    }
                }

                result.Add((JsonNode?)JsonValue.Create(name));
            }

            return result;
        }

        var chars = content["characters"]?.AsArray()
            ?? throw new ModOperationException("content.json 缺少 characters 字段");

        foreach (var (key, value) in decks)
        {
            var idx = int.Parse(key);
            if (chars[idx] is JsonObject chObj && value is JsonArray deckArr)
            {
                chObj["cards"] = ResolveNames(deckArr);
            }
        }

        if (mod["variant7"] is JsonArray variant7 &&
            chars[7] is JsonObject char7 &&
            char7["alternate"] is JsonObject alt7)
        {
            alt7["cards"] = ResolveNames(variant7);
        }

        if (mod["mage"] is JsonObject mage)
        {
            var mageInfo = content["artifactInfo"]?["mage"]?.AsObject();
            if (mageInfo is not null)
            {
                if (mageInfo["parameters"] is not JsonObject parameters)
                {
                    parameters = new JsonObject();
                    mageInfo["parameters"] = parameters;
                }

                parameters["dark_magician_girl"] = mage["value"]!.GetValue<int>();
            }

            if (content["artifacts"]?["mage"] is JsonArray mageArtifact && mageArtifact.Count > 2)
            {
                mageArtifact[2] = mage["descNew"]!.GetValue<string>();
                interaction.Say("content.json: 魔术师之杖已追加「黑魔术少女 +300」说明");
            }
        }

        // Starting relics (e.g. Marik starts with Radiance of Ra)
        if (mod["startingRelics"] is JsonArray startingRelics)
        {
            foreach (var item in startingRelics)
            {
                if (item is not JsonObject sr) continue;
                var idx = sr["index"]!.GetValue<int>();
                var relicKey = sr["relic"]!.GetValue<string>();
                if (chars[idx] is JsonObject chObj)
                {
                    chObj["starting_relic"] = relicKey;
                }
            }
        }

        // New relic: Radiance of Ra (art is a data: URL allowed by patched ui-art.js)
        if (mod["relic"] is JsonObject relic)
        {
            var key = relic["key"]!.GetValue<string>();
            content["artifacts"]![key] = relic["entry"]!.DeepClone();
            content["artifactInfo"]![key] = relic["info"]!.DeepClone();
            var artLength = relic["info"]?["art"]?.GetValue<string>().Length ?? 0;
            interaction.Say($"content.json: 遗物 {key} 已写入，卡图内联 {artLength} 字符");
        }

        // Register new booster pack in content.json so the shop UI can resolve its name
        if (mod["pack"] is JsonObject pack)
        {
            var packId = pack["id"]!.GetValue<string>();
            var newPacks = new JsonArray();
            if (content["packs"] is JsonArray existingPacks)
            {
                foreach (var p in existingPacks)
                {
                    if (p is JsonObject pObj &&
                        !string.Equals(pObj["id"]?.GetValue<string>(), packId, StringComparison.Ordinal))
                    {
                        newPacks.Add(pObj.DeepClone());
                    }
                }
            }

            newPacks.Add(pack.DeepClone());
            content["packs"] = newPacks;
            var commonCount = pack["common"]?.AsArray().Count ?? 0;
            interaction.Say($"content.json: 新卡包 {packId} 已列入商店（{commonCount} 张卡）");
        }

        if (rdRecovered > 0)
        {
            interaction.Say($"content.json: 从自带卡表补回 {rdRecovered} 张原版卡（本地卡池比预期小）");
        }

        if (dropped.Count > 0)
        {
            interaction.Warn("这些卡在任何卡表里都没有，已从卡组里去掉：" + string.Join("、", dropped.Distinct(StringComparer.Ordinal)));
        }

        interaction.Say($"content.json: +{added} 张卡，{deckCount} 套起始卡组 + 帕加索斯变体已改写");
    }

    public static void PatchEngine(JsonObject engine, JsonObject mod, IInstallerInteraction interaction)
    {
        var added = 0;
        if (mod["engine"] is JsonObject modEngine)
        {
            foreach (var (key, value) in modEngine)
            {
                if (!engine.ContainsKey(key))
                {
                    engine[key] = value?.DeepClone();
                    added++;
                }
            }
        }

        if (mod["fallbackEngine"] is JsonObject fallbackEngine)
        {
            foreach (var (key, value) in fallbackEngine)
            {
                if (!engine.ContainsKey(key))
                {
                    engine[key] = value?.DeepClone();
                    added++;
                }
            }
        }

        interaction.Say($"engine-data.json: +{added} 行");
    }

    public static void PatchScripts(JsonObject scripts, JsonObject mod, IInstallerInteraction interaction)
    {
        var added = 0;
        if (mod["scripts"] is JsonObject modScripts)
        {
            foreach (var (key, value) in modScripts)
            {
                if (!scripts.ContainsKey(key))
                {
                    added++;
                }

                scripts[key] = value?.DeepClone();
            }
        }

        var util = scripts["utility.lua"]?.GetValue<string>() ?? string.Empty;
        var getidOld = mod["getidOld"]!.GetValue<string>();
        var getidNew = mod["getidNew"]!.GetValue<string>();

        if (util.Contains(getidOld, StringComparison.Ordinal))
        {
            util = util.Replace(getidOld, getidNew, StringComparison.Ordinal);
            interaction.Say("utility.lua: GetID 改为返回 3 个值");
        }
        else if (!util.Contains("local offset=self_code<100000000", StringComparison.Ordinal))
        {
            throw new ModOperationException("utility.lua 里找不到 GetID 的定义");
        }

        if (!util.Contains("-- rd-shim.lua", StringComparison.Ordinal))
        {
            util = util + "\n" + mod["shim"]!.GetValue<string>();
            interaction.Say("utility.lua: 已追加兼容层");
        }

        scripts["utility.lua"] = util;
        interaction.Say($"scripts-0.json: +{added} 个卡脚本");

        if (mod["helpers"] is JsonObject helpers)
        {
            var helperAdded = 0;
            foreach (var (key, value) in helpers)
            {
                if (!scripts.ContainsKey(key))
                {
                    helperAdded++;
                }

                scripts[key] = value?.DeepClone();
            }

            if (helperAdded > 0)
            {
                interaction.Say($"scripts-0.json: 额外辅助脚本 +{helperAdded} 个（衍生物等）");
            }
        }
    }

    private static string AddCodes(string src, string name, IReadOnlyList<int> codes, IInstallerInteraction interaction)
    {
        var match = Regex.Match(src, name + @"=\(([^)]*)\)");
        if (!match.Success)
        {
            interaction.Warn($"campaign-files.json 里找不到 {name}");
            return src;
        }

        var cur = match.Groups[1].Value
            .Split(',')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        var curSet = new HashSet<string>(cur, StringComparer.OrdinalIgnoreCase);
        var toAdd = new List<string>();
        foreach (var c in codes)
        {
            var s = c.ToString();
            if (!curSet.Contains(s))
            {
                toAdd.Add(s);
            }
        }

        if (toAdd.Count == 0)
        {
            return src;
        }

        var replacement = $"{name}=({string.Join(",", cur.Concat(toAdd))})";
        return src.Replace(match.Value, replacement, StringComparison.Ordinal);
    }

    private static string ToPythonStringLiteral(string value)
    {
        var escapedBackslash = value.Replace("\\", "\\\\", StringComparison.Ordinal);
        if (escapedBackslash.Contains('\'') && !escapedBackslash.Contains('"'))
        {
            return $"\"{escapedBackslash}\"";
        }

        var escapedSingle = escapedBackslash.Replace("'", "\\'", StringComparison.Ordinal);
        return $"'{escapedSingle}'";
    }

    private static string AddNames(string src, string name, IReadOnlyList<string> names, IInstallerInteraction interaction)
    {
        var match = Regex.Match(src, name + @"=\{([^}]*)\}");
        if (!match.Success)
        {
            interaction.Warn($"campaign-files.json 里找不到 {name}");
            return src;
        }

        var body = match.Groups[1].Value;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toAdd = new List<string>();
        foreach (var raw in names)
        {
            var n = raw?.Trim();
            // Skip malformed fragments from legacy mod.json (e.g. "," or broken quote fragments)
            if (string.IsNullOrEmpty(n) || n == "," || n.Contains("\",", StringComparison.Ordinal))
            {
                continue;
            }

            if (!seen.Add(n))
            {
                continue;
            }

            var literal = ToPythonStringLiteral(n);
            var singleForm = $"'{n.Replace("\\", "\\\\").Replace("'", "\\'")}'";
            var doubleForm = $"\"{n.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

            if (!Regex.IsMatch(body, Regex.Escape(singleForm), RegexOptions.IgnoreCase) &&
                !Regex.IsMatch(body, Regex.Escape(doubleForm), RegexOptions.IgnoreCase) &&
                !Regex.IsMatch(body, Regex.Escape($"'{n}'"), RegexOptions.IgnoreCase) &&
                !Regex.IsMatch(body, Regex.Escape($"\"{n}\""), RegexOptions.IgnoreCase))
            {
                toAdd.Add(literal);
            }
        }

        if (toAdd.Count == 0)
        {
            return src;
        }

        var replacement = $"{name}={{{body},{string.Join(",", toAdd)}}}";
        return src.Replace(match.Value, replacement, StringComparison.Ordinal);
    }

    public static void PatchCampaign(JsonObject campaign, JsonObject mod, IInstallerInteraction interaction)
    {
        // 1. data/era-cards.json
        var era = ParseJson(campaign["data/era-cards.json"]!.GetValue<string>()).AsArray();
        var eraSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in era)
        {
            if (row is JsonObject rowObj)
            {
                eraSeen.Add(NodeAsKeyString(rowObj["id"]));
            }
        }

        var added = 0;
        if (mod["eraRows"] is JsonArray eraRows)
        {
            foreach (var row in eraRows)
            {
                if (row is not JsonObject rowObj) continue;
                var id = NodeAsKeyString(rowObj["id"]);
                if (eraSeen.Add(id))
                {
                    era.Add(rowObj.DeepClone());
                    added++;
                }
            }
        }

        campaign["data/era-cards.json"] = ToJsonText(era);

        // 2. data/packs.json
        var packs = ParseJson(campaign["data/packs.json"]!.GetValue<string>()).AsArray();
        var modPack = mod["pack"]!.AsObject();
        var modPackId = modPack["id"]!.GetValue<string>();
        var packList = new JsonArray();
        var hasPack = false;
        foreach (var p in packs)
        {
            if (p is JsonObject pObj && string.Equals(pObj["id"]?.GetValue<string>(), modPackId, StringComparison.Ordinal))
            {
                hasPack = true;
                packList.Add(modPack.DeepClone());
            }
            else if (p is not null)
            {
                packList.Add(p.DeepClone());
            }
        }

        if (!hasPack)
        {
            packList.Add(modPack.DeepClone());
        }

        campaign["data/packs.json"] = ToJsonText(packList);

        // 3. data/character-signatures.json
        var sig = ParseJson(campaign["data/character-signatures.json"]!.GetValue<string>()).AsObject();
        var decks = mod["decks"]!.AsObject();
        foreach (var (key, value) in decks)
        {
            sig[key] = value?.DeepClone();
        }

        campaign["data/character-signatures.json"] = ToJsonText(sig);

        // 4. data/character-variants.json
        var variants = ParseJson(campaign["data/character-variants.json"]!.GetValue<string>()).AsObject();
        variants["variants"]!["7"]!["cards"] = mod["variant7"]!.DeepClone();
        campaign["data/character-variants.json"] = ToJsonText(variants);

        // 5. data/character-progression.json
        var prog = ParseJson(campaign["data/character-progression.json"]!.GetValue<string>()).AsObject();
        prog["unlocks"]!["7"] = mod["unlockRule7"]!.DeepClone();
        campaign["data/character-progression.json"] = ToJsonText(prog);

        // 6. storage.py
        var storage = campaign["storage.py"]!.GetValue<string>();
        var storageNew = mod["storageNew"]!.GetValue<string>();
        storage = Regex.Replace(storage, @"'unlocked':\[\d+(?:,\d+)*\]", storageNew);
        if (!storage.Contains(storageNew, StringComparison.Ordinal))
        {
            throw new ModOperationException("storage.py 里找不到 unlocked 默认值");
        }

        if (!storage.Contains("p['unlocked']=sorted(", StringComparison.Ordinal))
        {
            const string union = "\n p['unlocked']=sorted(set(p.get('unlocked') or [])|set([0,1,7,30]))";
            storage = storage.Replace("p.setdefault(k,v)", "p.setdefault(k,v)" + union, StringComparison.Ordinal);
        }

        campaign["storage.py"] = storage;

        // 7. character_variants.py
        var cv = campaign["character_variants.py"]!.GetValue<string>();
        if (!cv.Contains("if int(index)==7:return True", StringComparison.Ordinal))
        {
            cv = cv.Replace(
                "def unlocked(profile,index):",
                "def unlocked(profile,index):\n if int(index)==7:return True",
                StringComparison.Ordinal);
        }

        campaign["character_variants.py"] = cv;

        // 8. Relic card lists in approved_relic_effects.py & shop_rewards.py
        var eff = campaign["approved_relic_effects.py"]!.GetValue<string>();
        var relicLists = mod["relicLists"]!.AsObject();
        eff = AddCodes(eff, "DARK_MAGICIAN_MENTIONS", ToIntList(relicLists["mentions"]), interaction);
        eff = AddCodes(eff, "DARK_MAGICIANS", ToIntList(relicLists["monsters"]), interaction);
        eff = AddCodes(eff, "EYES", ToIntList(relicLists["eyes"]), interaction);
        campaign["approved_relic_effects.py"] = eff;

        var shop = campaign["shop_rewards.py"]!.GetValue<string>();
        shop = AddNames(shop, "DM_SUPPORT", ToStringList(relicLists["dmSupport"]), interaction);
        campaign["shop_rewards.py"] = shop;

        // 9. Toon World Chinese-name recognition patches
        var toonAdded = 0;
        if (mod["toonPatch"] is JsonArray toonPatch)
        {
            foreach (var item in toonPatch)
            {
                if (item is not JsonObject tp) continue;
                var file = tp["file"]!.GetValue<string>();
                var text = campaign[file]!.GetValue<string>();
                var newText = tp["new"]!.GetValue<string>();
                var oldText = tp["old"]!.GetValue<string>();
                if (!text.Contains(newText, StringComparison.Ordinal))
                {
                    text = text.Replace(oldText, newText, StringComparison.Ordinal);
                    campaign[file] = text;
                    toonAdded++;
                }
            }
        }

        if (mod["toonThemeId"] is JsonValue toonThemeVal)
        {
            var toonThemeId = toonThemeVal.GetValue<int>();
            var themes = ParseJson(campaign["data/relic-theme-cards.json"]!.GetValue<string>()).AsObject();
            var list = themes["toon_world"]!.AsArray();
            var exists = list.Any(cid => cid is JsonValue v && v.TryGetValue<int>(out var n) && n == toonThemeId);
            if (!exists)
            {
                list.Add((JsonNode?)JsonValue.Create(toonThemeId));
                toonAdded++;
            }

            campaign["data/relic-theme-cards.json"] = ToJsonText(themes);
        }

        if (toonAdded > 0)
        {
            interaction.Say($"campaign-files.json: 卡通世界识别补丁 +{toonAdded} 处");
        }

        // 10. Starting relics (progression json + character_variants.py)
        if (mod["startingRelics"] is JsonArray startingRelics)
        {
            foreach (var item in startingRelics)
            {
                if (item is not JsonObject sr) continue;
                var idx = sr["index"]!.GetValue<int>();
                var rkey = sr["relic"]!.GetValue<string>();

                var pr = ParseJson(campaign["data/character-progression.json"]!.GetValue<string>()).AsObject();
                pr["starting_relics"]![idx.ToString()] = rkey;
                campaign["data/character-progression.json"] = ToJsonText(pr);

                var cvs = campaign["character_variants.py"]!.GetValue<string>();
                var pattern = $@"characters\[{idx}\]\['starting_relic'\]='[A-Za-z_]+'";
                var repl = $"characters[{idx}]['starting_relic']='{rkey}'";
                if (Regex.IsMatch(cvs, pattern))
                {
                    cvs = Regex.Replace(cvs, pattern, repl);
                    campaign["character_variants.py"] = cvs;
                }

                interaction.Say($"起始遗物：角色 {idx} -> {rkey}");
            }
        }

        // 11. New relic: Radiance of Ra (REGULAR + effect() branch)
        if (mod["relic"] is JsonObject relic)
        {
            var rkey = relic["key"]!.GetValue<string>();
            var mark = "# rd-relic:" + rkey;
            var rpy = campaign["approved_relics.py"]!.GetValue<string>();
            if (!rpy.Contains(mark, StringComparison.Ordinal))
            {
                campaign["approved_relics.py"] = rpy.TrimEnd() + "\n\n" + mark + mod["relicPython"]!["relics"]!.GetValue<string>();
            }

            var epy = campaign["approved_relic_effects.py"]!.GetValue<string>();
            if (!epy.Contains(mark, StringComparison.Ordinal))
            {
                campaign["approved_relic_effects.py"] = epy.TrimEnd() + "\n\n" + mark + mod["relicPython"]!["effects"]!.GetValue<string>();
            }

            interaction.Say($"campaign-files.json: 遗物 {rkey} 已注册（REGULAR + effect 分支）");
        }

        // 12. Magician Staff bonus for Dark Magician Girl
        if (mod["mage"] is JsonObject mage)
        {
            var tuningText = campaign["data/tuning.json"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(tuningText))
            {
                var tuning = ParseJson(tuningText).AsObject();
                var mageEntry = tuning["relics"]?["mage"]?.AsObject();
                if (mageEntry is not null)
                {
                    if (mageEntry["parameters"] is not JsonObject parameters)
                    {
                        parameters = new JsonObject();
                        mageEntry["parameters"] = parameters;
                    }

                    parameters["dark_magician_girl"] = mage["value"]!.GetValue<int>();
                    mageEntry["description"] = mage["descNew"]!.GetValue<string>();
                    campaign["data/tuning.json"] = ToJsonText(tuning);
                }
            }

            var pass = campaign["passives.py"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(pass) && !pass.Contains("rd-mage:dark_magician_girl", StringComparison.Ordinal))
            {
                campaign["passives.py"] = pass.Replace(
                    mage["passivesOld"]!.GetValue<string>(),
                    mage["passivesNew"]!.GetValue<string>(),
                    StringComparison.Ordinal);
                interaction.Say("campaign-files.json: 魔术师之杖「黑魔术少女 +300」效果已写入");
            }
        }

        interaction.Say($"campaign-files.json: +{added} 行卡池、新卡包、{decks.Count} 套签名卡、帕加索斯变体、默认解锁、遗物卡表");
    }

    public static string PatchCardAssets(
        string text,
        JsonObject mod,
        string rootDir,
        string? picsDir,
        IInstallerInteraction interaction)
    {
        const string trustedOld = "cardImages.set(id,`assets/cards/${id}.jpg`)";
        const string trustedNew = "cardImages.set(id,INLINE_ART[id]||`assets/cards/${id}.jpg`)";
        if (!text.Contains(trustedOld, StringComparison.Ordinal) &&
            !text.Contains(trustedNew, StringComparison.Ordinal))
        {
            throw new ModOperationException("card-assets.js 结构与预期不同（trusted 分支）");
        }

        text = text.Replace(trustedOld, trustedNew, StringComparison.Ordinal);

        const string workerAnchor = "  const path=`assets/cards/${id}.jpg`;";
        if (!text.Contains(workerAnchor, StringComparison.Ordinal))
        {
            throw new ModOperationException("card-assets.js 结构与预期不同（worker）");
        }

        const string shortcut = "  if(INLINE_ART[id]){cardImages.set(id,INLINE_ART[id]);loaded++;processed++;report();continue}\n";
        text = text.Replace(workerAnchor, shortcut + workerAnchor, StringComparison.Ordinal);

        var sb = new StringBuilder();
        sb.Append("const INLINE_ART={");
        var first = true;
        var artCount = 0;
        var missing = new List<string>();
        var artDir = Path.Combine(rootDir, "art");
        string[] extensions = ["webp", "jpg", "png"];

        if (mod["codes"] is JsonArray codes)
        {
            foreach (var codeNode in codes)
            {
                var code = NodeAsKeyString(codeNode);
                string? bundled = null;
                foreach (var ext in extensions)
                {
                    var candidate = Path.Combine(artDir, $"{code}.{ext}");
                    if (File.Exists(candidate))
                    {
                        bundled = candidate;
                        break;
                    }
                }

                if (bundled is not null)
                {
                    var mime = Path.GetExtension(bundled).ToLowerInvariant() switch
                    {
                        ".webp" => "image/webp",
                        ".png" => "image/png",
                        _ => "image/jpeg"
                    };

                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(code).Append("\":\"data:").Append(mime).Append(";base64,");
                    sb.Append(Convert.ToBase64String(File.ReadAllBytes(bundled)));
                    sb.Append('"');
                    artCount++;
                    continue;
                }

                var picFile = string.IsNullOrWhiteSpace(picsDir) ? null : Path.Combine(picsDir, $"{code}.jpg");
                if (picFile is null || !File.Exists(picFile))
                {
                    missing.Add(code);
                    continue;
                }

                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(code).Append("\":\"data:image/jpeg;base64,");
                sb.Append(CardArtEncoder.ConvertArtToBase64(picFile));
                sb.Append('"');
                artCount++;
            }
        }

        sb.Append("};\n");
        if (missing.Count > 0)
        {
            interaction.Warn("这些卡没找到卡图，会显示空白：" + string.Join(", ", missing));
        }

        interaction.Say($"card-assets.js: 内联卡图 {artCount} 张");
        return sb.ToString() + text;
    }

    public static string PatchZh(string text, JsonObject mod, IInstallerInteraction interaction)
    {
        var zh = ParseJson(text).AsObject();
        var cards = zh["cards"]?.AsObject()
            ?? throw new ModOperationException("i18n/zh.json 缺少 cards 字段");

        var added = 0;
        if (mod["zh"] is JsonObject modZh)
        {
            foreach (var (key, value) in modZh)
            {
                if (!cards.ContainsKey(key))
                {
                    added++;
                }

                cards[key] = value?.DeepClone();
            }
        }

        if (mod["fallbackZh"] is JsonObject fallbackZh)
        {
            foreach (var (key, value) in fallbackZh)
            {
                if (!cards.ContainsKey(key))
                {
                    added++;
                    cards[key] = value?.DeepClone();
                }
            }
        }

        if (mod["mage"] is JsonObject mage && zh["ui"] is JsonObject mageUi)
        {
            var zhOld = mage["zhOld"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(zhOld))
            {
                mageUi.Remove(zhOld);
            }

            mageUi[mage["descNew"]!.GetValue<string>()] = mage["zhNew"]!.GetValue<string>();
        }

        var uiAdded = 0;
        if (mod["relicUi"] is JsonObject relicUi && zh["ui"] is JsonObject ui)
        {
            foreach (var (key, value) in relicUi)
            {
                if (!ui.ContainsKey(key))
                {
                    uiAdded++;
                }

                ui[key] = value?.DeepClone();
            }
        }

        interaction.Say($"i18n/zh.json: 中文名/卡文 +{added} 条，遗物 ui +{uiAdded} 条");
        return ToJsonText(zh);
    }

    public static string PatchUiArt(string text)
    {
        if (text.Contains("String(id).startsWith('data:')", StringComparison.Ordinal))
        {
            return text;
        }

        const string oldStr = "export const relicImage=id=>assetURL(";
        if (!text.Contains(oldStr, StringComparison.Ordinal))
        {
            throw new ModOperationException("ui-art.js 结构与预期不同（relicImage）");
        }

        const string newStr = "export const relicImage=id=>String(id).startsWith('data:')?id:assetURL(";
        return text.Replace(oldStr, newStr, StringComparison.Ordinal);
    }

    public static string PatchDifficulty(string text)
    {
        const string oldStr = "return !!profile.variants_unlocked||";
        const string newStr = "return character===7||!!profile.variants_unlocked||";
        if (text.Contains(newStr, StringComparison.Ordinal))
        {
            return text;
        }

        if (!text.Contains(oldStr, StringComparison.Ordinal))
        {
            throw new ModOperationException("difficulty-preference.js 结构与预期不同");
        }

        return text.Replace(oldStr, newStr, StringComparison.Ordinal);
    }

    public static string PatchServiceWorker(
        string swText,
        IReadOnlyDictionary<string, byte[]> files,
        IInstallerInteraction interaction)
    {
        var stamp = new StringBuilder();
        foreach (var rel in PatchedAssetNames)
        {
            var hash = ComputeSha256Hex(files[rel])[..16];
            var pattern = $"\"{Regex.Escape(rel)}\":\"[0-9a-f]{{16}}\"";
            if (!Regex.IsMatch(swText, pattern))
            {
                throw new ModOperationException($"sw.js 里没有 {rel} 的哈希条目");
            }

            swText = Regex.Replace(swText, pattern, $"\"{rel}\":\"{hash}\"");
            stamp.Append(hash);
        }

        var version2 = ComputeSha256Hex(GetUtf8Bytes(stamp.ToString()))[..16];
        swText = Regex.Replace(swText, @"const VERSION=""[0-9a-f]{16}""", $"const VERSION=\"{version2}\"");
        interaction.Say($"sw.js: 已更新 {PatchedAssetNames.Length} 个哈希，缓存版本 {version2}");
        return swText;
    }

    private static string NodeAsKeyString(JsonNode? node)
    {
        if (node is null) return string.Empty;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var s)) return s;
            if (value.TryGetValue<long>(out var l)) return l.ToString();
            if (value.TryGetValue<int>(out var i)) return i.ToString();
            return value.ToJsonString();
        }

        return node.ToJsonString();
    }

    private static List<int> ToIntList(JsonNode? node)
    {
        var list = new List<int>();
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonValue v && v.TryGetValue<int>(out var n))
                {
                    list.Add(n);
                }
            }
        }

        return list;
    }

    private static List<string> ToStringList(JsonNode? node)
    {
        var list = new List<string>();
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                list.Add(NodeAsKeyString(item));
            }
        }

        return list;
    }
}
