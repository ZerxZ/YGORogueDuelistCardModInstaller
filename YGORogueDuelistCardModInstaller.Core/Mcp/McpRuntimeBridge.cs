namespace YGORogueDuelistCardModInstaller.Core.Mcp;

public sealed class StudioFocusContext
{
    public string? WorkspaceRoot { get; set; }
    public string? ModId { get; set; }
    public string? ModName { get; set; }
    public string? Category { get; set; }
    public string? ItemKey { get; set; }
    public string? ItemName { get; set; }
    public string? ScriptPath { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record McpCallRecord(
    string Id,
    string ToolName,
    string ArgsSummary,
    bool Ok,
    long DurationMs,
    string ResultPreview,
    DateTimeOffset TimestampUtc);

/// <summary>
/// Optional live host hooks allowing the Desktop Studio's built-in MCP server to share
/// active workspace, focused card/script context, instant cache invalidation, and Hot-Reload.
/// Standalone stdio MCP leaves all hooks null.
/// </summary>
public sealed class McpHostHooks
{
    public Func<string?>? GetDefaultWorkspaceRoot { get; init; }
    public Func<StudioFocusContext?>? GetStudioFocus { get; init; }
    public Func<string?, string?, CancellationToken, Task<string>>? OnTriggerHotReloadAsync { get; init; }
    public Action<string, string?, string>? OnModMutated { get; init; }
    public Action<McpCallRecord>? OnToolCalled { get; init; }
}
