using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace YGORogueDuelistCardModInstaller.Core;

public sealed record ManifestSlice(int Start, int Length, string Text, Dictionary<string, string> Map);

public static class ManifestPatcher
{
    public const string Marker = "{\"activation-reasons.js\"";
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static ManifestSlice ReadManifest(byte[] dllBytes)
    {
        // Primary: locate the exact embedded "StartupHashes.json" resource via PE metadata
        var resourceSpan = ModManager.LauncherInspector.TryLocateEmbeddedResource(dllBytes, "StartupHashes.json");
        if (resourceSpan is { } loc)
        {
            var resText = Encoding.UTF8.GetString(dllBytes, loc.Offset, loc.Length);
            var resObj = JsonNode.Parse(resText)?.AsObject()
                ?? throw new ModOperationException("无法解析启动器 StartupHashes.json 清单");
            var resMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in resObj)
            {
                resMap[kv.Key] = kv.Value?.GetValue<string>() ?? string.Empty;
            }

            return new ManifestSlice(loc.Offset, loc.Length, resText, resMap);
        }

        // Fallback: scan for legacy marker
        var needle = Encoding.ASCII.GetBytes(Marker);
        var start = dllBytes.AsSpan().IndexOf(needle);
        if (start < 0)
        {
            throw new ModOperationException("找不到启动器里的卡片清单");
        }

        var depth = 0;
        var end = -1;
        var inString = false;

        for (var i = start; i < dllBytes.Length; i++)
        {
            var ch = (char)dllBytes[i];
            if (inString)
            {
                if (ch == '\\')
                {
                    i++;
                }
                else if (ch == '"')
                {
                    inString = false;
                }
            }
            else
            {
                if (ch == '"')
                {
                    inString = true;
                }
                else if (ch == '{')
                {
                    depth++;
                }
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        end = i;
                        break;
                    }
                }
            }
        }

        if (end < 0)
        {
            throw new ModOperationException("启动器清单不完整");
        }

        var length = end - start + 1;
        var text = Encoding.UTF8.GetString(dllBytes, start, length);
        var jsonObj = JsonNode.Parse(text)?.AsObject()
            ?? throw new ModOperationException("无法解析启动器清单 JSON");

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in jsonObj)
        {
            map[kv.Key] = kv.Value?.GetValue<string>() ?? string.Empty;
        }

        return new ManifestSlice(start, length, text, map);
    }

    public static void PatchLauncherDll(
        string dllPath,
        string backupDir,
        IReadOnlyDictionary<string, byte[]> files,
        IInstallerInteraction interaction)
    {
        var dllBytes = File.ReadAllBytes(dllPath);
        var manifest = ReadManifest(dllBytes);
        interaction.Say($"启动器清单：{manifest.Map.Count} 条");

        var text = manifest.Text;
        var missing = new List<string>();

        foreach (var (rel, fileBytes) in files)
        {
            if (!manifest.Map.TryGetValue(rel, out var oldHash) || oldHash.Length != 64)
            {
                missing.Add(rel);
                continue;
            }

            var newHash = ModPatcher.ComputeSha256Hex(fileBytes);
            if (string.Equals(oldHash, newHash, StringComparison.Ordinal))
            {
                continue;
            }

            var pattern = $"\"{Regex.Escape(rel)}\":\"{oldHash}\"";
            text = Regex.Replace(text, pattern, $"\"{rel}\":\"{newHash}\"");
        }

        if (missing.Count > 0)
        {
            throw new ModOperationException("这些文件不在启动器清单里：" + string.Join(", ", missing));
        }

        var newBytes = Utf8NoBom.GetBytes(text);
        if (newBytes.Length != manifest.Length)
        {
            throw new ModOperationException("补丁后的清单长度变化了，版本可能不匹配。");
        }

        Directory.CreateDirectory(backupDir);
        var backupFile = Path.Combine(backupDir, "launcher.dll");
        if (!File.Exists(backupFile))
        {
            File.Copy(dllPath, backupFile, overwrite: true);
            interaction.Say(@"已备份原启动器 -> backup\launcher.dll");
        }

        var copy = (byte[])dllBytes.Clone();
        Buffer.BlockCopy(newBytes, 0, copy, manifest.Start, newBytes.Length);
        File.WriteAllBytes(dllPath, copy);
        interaction.Ok($"启动器清单已更新（{files.Count} 个文件）");
    }
}
