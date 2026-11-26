using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YGORogueDuelistCardModInstaller.Core.ModManager;

public sealed class WorkspaceProfile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("lastUsedUtc")]
    public DateTimeOffset LastUsedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WorkspaceRegistryData
{
    [JsonPropertyName("activeWorkspaceId")]
    public string? ActiveWorkspaceId { get; set; }

    [JsonPropertyName("workspaces")]
    public List<WorkspaceProfile> Workspaces { get; set; } = [];
}

/// <summary>
/// Manages multiple MOD workspaces globally under %LOCALAPPDATA%\YGORogueDuelistModManager\workspaces.json.
/// 100% Native AOT compatible via CoreJsonContext.
/// </summary>
public sealed class WorkspaceRegistry
{
    public string RegistryFilePath { get; }

    public WorkspaceRegistry(string? customRegistryFilePath = null)
    {
        if (!string.IsNullOrWhiteSpace(customRegistryFilePath))
        {
            RegistryFilePath = Path.GetFullPath(customRegistryFilePath);
        }
        else
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            RegistryFilePath = Path.Combine(localAppData, "YGORogueDuelistModManager", "workspaces.json");
        }
    }

    public WorkspaceRegistryData Load()
    {
        if (!File.Exists(RegistryFilePath))
        {
            return new WorkspaceRegistryData();
        }

        try
        {
            var text = File.ReadAllText(RegistryFilePath, Encoding.UTF8);
            return JsonSerializer.Deserialize(text, CoreJsonContext.PrettyUtf8.WorkspaceRegistryData)
                   ?? new WorkspaceRegistryData();
        }
        catch
        {
            return new WorkspaceRegistryData();
        }
    }

    public void Save(WorkspaceRegistryData data)
    {
        var dir = Path.GetDirectoryName(RegistryFilePath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(data, CoreJsonContext.PrettyUtf8.WorkspaceRegistryData);
        File.WriteAllText(RegistryFilePath, json, ModPatcher.Utf8NoBom);
    }

    public WorkspaceProfile EnsureRegistered(string workspacePath, string? name = null, string? description = null, bool setActive = true)
    {
        var fullPath = NormalizePath(workspacePath);
        var data = Load();
        var existing = data.Workspaces.FirstOrDefault(w =>
            string.Equals(NormalizePath(w.Path), fullPath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            if (!string.IsNullOrWhiteSpace(name)) existing.Name = name.Trim();
            if (description is not null) existing.Description = description.Trim();
            existing.LastUsedUtc = DateTimeOffset.UtcNow;
            if (setActive) data.ActiveWorkspaceId = existing.Id;
            Save(data);
            return existing;
        }

        var leaf = Path.GetFileName(fullPath);
        var displayName = string.IsNullOrWhiteSpace(name)
            ? (string.IsNullOrWhiteSpace(leaf) ? "Default Workspace" : leaf)
            : name.Trim();
        var baseId = ModRepository.SanitizeId(displayName);
        if (string.IsNullOrWhiteSpace(baseId)) baseId = "workspace";
        var id = baseId;
        var suffix = 2;
        while (data.Workspaces.Any(w => string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            id = $"{baseId}-{suffix++}";
        }

        var profile = new WorkspaceProfile
        {
            Id = id,
            Name = displayName,
            Path = fullPath,
            Description = description?.Trim() ?? string.Empty,
            LastUsedUtc = DateTimeOffset.UtcNow
        };
        data.Workspaces.Add(profile);
        if (setActive || string.IsNullOrWhiteSpace(data.ActiveWorkspaceId))
        {
            data.ActiveWorkspaceId = profile.Id;
        }

        Save(data);
        return profile;
    }

    public WorkspaceProfile CreateWorkspace(string name, string workspacePath, string? description = null, bool setActive = true)
    {
        var fullPath = NormalizePath(workspacePath);
        Directory.CreateDirectory(fullPath);
        Directory.CreateDirectory(Path.Combine(fullPath, "mods"));
        return EnsureRegistered(fullPath, name, description, setActive);
    }

    public WorkspaceProfile RenameWorkspace(string idOrPath, string newName, string? newDescription = null)
    {
        var data = Load();
        var target = FindProfile(data, idOrPath)
            ?? throw new ModOperationException($"找不到工作区：{idOrPath}");

        if (!string.IsNullOrWhiteSpace(newName)) target.Name = newName.Trim();
        if (newDescription is not null) target.Description = newDescription.Trim();
        Save(data);
        return target;
    }

    public WorkspaceProfile SetActiveWorkspace(string idOrNameOrPath)
    {
        var data = Load();
        var target = FindProfile(data, idOrNameOrPath)
            ?? throw new ModOperationException($"找不到工作区：{idOrNameOrPath}");

        Directory.CreateDirectory(target.Path);
        Directory.CreateDirectory(Path.Combine(target.Path, "mods"));
        target.LastUsedUtc = DateTimeOffset.UtcNow;
        data.ActiveWorkspaceId = target.Id;
        Save(data);
        return target;
    }

    public bool RemoveWorkspace(string idOrNameOrPath)
    {
        var data = Load();
        var target = FindProfile(data, idOrNameOrPath);
        if (target is null) return false;

        data.Workspaces.Remove(target);
        if (string.Equals(data.ActiveWorkspaceId, target.Id, StringComparison.OrdinalIgnoreCase))
        {
            data.ActiveWorkspaceId = data.Workspaces
                .OrderByDescending(w => w.LastUsedUtc)
                .FirstOrDefault()?.Id;
        }

        Save(data);
        return true;
    }

    public List<WorkspaceProfile> ListWorkspaces(string? ensureDefaultPath = null)
    {
        var data = Load();
        if (data.Workspaces.Count == 0 && !string.IsNullOrWhiteSpace(ensureDefaultPath))
        {
            EnsureRegistered(ensureDefaultPath, "默认工作区 / Default", setActive: true);
            data = Load();
        }
        else if (!string.IsNullOrWhiteSpace(ensureDefaultPath))
        {
            var norm = NormalizePath(ensureDefaultPath);
            if (!data.Workspaces.Any(w => string.Equals(NormalizePath(w.Path), norm, StringComparison.OrdinalIgnoreCase)))
            {
                EnsureRegistered(norm, setActive: false);
                data = Load();
            }
        }

        return data.Workspaces
            .OrderByDescending(w => string.Equals(w.Id, data.ActiveWorkspaceId, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(w => w.LastUsedUtc)
            .ToList();
    }

    public WorkspaceProfile? GetActiveWorkspace(string? fallbackPath = null)
    {
        var data = Load();
        var active = data.Workspaces.FirstOrDefault(w =>
            string.Equals(w.Id, data.ActiveWorkspaceId, StringComparison.OrdinalIgnoreCase))
            ?? data.Workspaces.OrderByDescending(w => w.LastUsedUtc).FirstOrDefault();

        if (active is null && !string.IsNullOrWhiteSpace(fallbackPath))
        {
            return EnsureRegistered(fallbackPath, "默认工作区 / Default", setActive: true);
        }

        return active;
    }

    private static WorkspaceProfile? FindProfile(WorkspaceRegistryData data, string query)
    {
        var q = query.Trim();
        return data.Workspaces.FirstOrDefault(w =>
                   string.Equals(w.Id, q, StringComparison.OrdinalIgnoreCase))
               ?? data.Workspaces.FirstOrDefault(w =>
                   string.Equals(w.Name, q, StringComparison.OrdinalIgnoreCase))
               ?? data.Workspaces.FirstOrDefault(w =>
                   string.Equals(NormalizePath(w.Path), NormalizePathSafe(q), StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePath(string p) =>
        Path.GetFullPath(p.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string NormalizePathSafe(string p)
    {
        try { return NormalizePath(p); }
        catch { return p.Trim(); }
    }
}
