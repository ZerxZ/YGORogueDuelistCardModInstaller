using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using YGORogueDuelistCardModInstaller.Core.ModManager;

namespace YGORogueDuelistCardModInstaller.Core;

internal sealed class GitHubReleaseDto
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("published_at")]
    public string? PublishedAt { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAssetDto>? Assets { get; set; }

    public GitHubRelease ToModel() => new()
    {
        TagName = TagName,
        Name = Name,
        Body = Body,
        PublishedAt = PublishedAt,
        HtmlUrl = HtmlUrl,
        Draft = Draft,
        Assets = Assets?.Select(a => new GitHubAsset
        {
            Name = a.Name,
            BrowserDownloadUrl = a.BrowserDownloadUrl,
            Size = a.Size,
            Digest = a.Digest
        }).ToList() ?? []
    };
}

internal sealed class GitHubAssetDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }
}

public sealed class ModRuntimeRegistryPayload
{
    [JsonPropertyName("runtimeVersion")]
    public string RuntimeVersion { get; set; } = ModRuntimeGenerator.RuntimeVersion;

    [JsonPropertyName("baseGameVersion")]
    public string BaseGameVersion { get; set; } = "unknown";

    [JsonPropertyName("generatedAtUtc")]
    public string GeneratedAtUtc { get; set; } = string.Empty;

    [JsonPropertyName("mods")]
    public List<ModRuntimeDescriptor> Mods { get; set; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(ModManifest))]
[JsonSerializable(typeof(WorkspacePackManifest))]
[JsonSerializable(typeof(WorkspaceProfile))]
[JsonSerializable(typeof(WorkspaceRegistryData))]
[JsonSerializable(typeof(ModManagerState))]
[JsonSerializable(typeof(ModEntryState))]
[JsonSerializable(typeof(LastInjectionRecord))]
[JsonSerializable(typeof(ModRuntimeRegistryPayload))]
[JsonSerializable(typeof(ModRuntimeDescriptor))]
[JsonSerializable(typeof(GitHubReleaseDto))]
[JsonSerializable(typeof(List<GitHubReleaseDto>))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonNode))]
internal partial class CoreJsonContext : JsonSerializerContext
{
    public static readonly CoreJsonContext PrettyUtf8 = new(new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
}
