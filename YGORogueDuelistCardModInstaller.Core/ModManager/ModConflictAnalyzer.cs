using System.Text;
using System.Text.Json.Nodes;

namespace YGORogueDuelistCardModInstaller.Core.ModManager;

public static class ModConflictAnalyzer
{
    public static List<ModConflict> Analyze(IReadOnlyList<ManagedMod> orderedMods)
    {
        var enabledMods = orderedMods.Where(m => m.Enabled).OrderBy(m => m.Order).ToList();
        if (enabledMods.Count <= 1)
        {
            return [];
        }

        var tracker = new Dictionary<(string Category, string Key), List<string>>();

        void Track(string category, string key, string modId)
        {
            var tuple = (category, key);
            if (!tracker.TryGetValue(tuple, out var list))
            {
                list = [];
                tracker[tuple] = list;
            }

            if (!list.Contains(modId, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(modId);
            }
        }

        foreach (var mod in enabledMods)
        {
            var legacyPath = Path.Combine(mod.DirectoryPath, "data", "mod.json");
            if (File.Exists(legacyPath))
            {
                try
                {
                    var json = JsonNode.Parse(File.ReadAllText(legacyPath, Encoding.UTF8))?.AsObject();
                    if (json is not null)
                    {
                        if (json["cards"] is JsonArray cards)
                        {
                            foreach (var c in cards)
                            {
                                var id = c?["id"]?.ToString();
                                if (!string.IsNullOrEmpty(id)) Track("卡片", id, mod.Id);
                            }
                        }

                        if (json["scripts"] is JsonObject scripts)
                        {
                            foreach (var (k, _) in scripts) Track("Lua脚本", k, mod.Id);
                        }

                        if (json["decks"] is JsonObject decks)
                        {
                            foreach (var (k, _) in decks) Track("角色卡组", $"角色#{k}", mod.Id);
                        }

                        if (json["relic"]?["key"]?.GetValue<string>() is { Length: > 0 } relicKey)
                        {
                            Track("遗物", relicKey, mod.Id);
                        }
                    }
                }
                catch { }
            }

            foreach (var cardFile in ModRepository.EnumerateDeclarativeCardFiles(mod.DirectoryPath))
            {
                foreach (var c in EnumerateJsonObjects(cardFile))
                {
                    var id = c["id"]?.ToString();
                    if (!string.IsNullOrEmpty(id)) Track("卡片", id, mod.Id);
                }
            }

            foreach (var charFile in ModRepository.EnumerateDeclarativeCharacterFiles(mod.DirectoryPath))
            {
                foreach (var ch in EnumerateJsonObjects(charFile))
                {
                    var key = ch["index"] is not null ? $"角色#{ch["index"]}" : ch["name"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(key)) Track("角色", key, mod.Id);
                }
            }

            foreach (var relicFile in ModRepository.EnumerateDeclarativeRelicFiles(mod.DirectoryPath))
            {
                foreach (var r in EnumerateJsonObjects(relicFile))
                {
                    var key = r["key"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(key)) Track("遗物", key, mod.Id);
                }
            }

            if (File.Exists(Path.Combine(mod.DirectoryPath, "trainer.json")))
            {
                Track("修改器", "trainer.json", mod.Id);
            }

            foreach (var f in ModRepository.EnumerateLuaCardScriptFiles(mod.DirectoryPath))
            {
                Track("Lua脚本", Path.GetFileName(f), mod.Id);
            }

            var decksPath = Path.Combine(mod.DirectoryPath, "decks.json");
            if (File.Exists(decksPath))
            {
                try
                {
                    var obj = JsonNode.Parse(File.ReadAllText(decksPath, Encoding.UTF8))?.AsObject();
                    if (obj is not null)
                    {
                        foreach (var (k, _) in obj) Track("角色卡组", $"角色#{k}", mod.Id);
                    }
                }
                catch { }
            }

            var artDir = Path.Combine(mod.DirectoryPath, "art");
            if (Directory.Exists(artDir))
            {
                foreach (var f in Directory.EnumerateFiles(artDir))
                {
                    Track("内联卡图", Path.GetFileNameWithoutExtension(f), mod.Id);
                }
            }

            var campaignDir = Path.Combine(mod.DirectoryPath, "campaign");
            if (Directory.Exists(campaignDir))
            {
                foreach (var f in Directory.EnumerateFiles(campaignDir, "*", SearchOption.AllDirectories))
                {
                    Track("战役文件", Path.GetRelativePath(campaignDir, f).Replace('\\', '/'), mod.Id);
                }
            }

            foreach (var rel in mod.Stats.OverrideRelativePaths)
            {
                Track("覆盖文件", rel, mod.Id);
            }
        }

        return tracker
            .Where(kv => kv.Value.Count > 1)
            .Select(kv => new ModConflict(kv.Key.Category, kv.Key.Key, kv.Value, kv.Value[^1]))
            .OrderBy(c => c.Category, StringComparer.Ordinal)
            .ThenBy(c => c.ResourceKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<JsonObject> EnumerateJsonObjects(string path)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
            return node switch
            {
                JsonArray arr => arr.OfType<JsonObject>().ToList(),
                JsonObject obj => [obj],
                _ => []
            };
        }
        catch
        {
            return [];
        }
    }
}
