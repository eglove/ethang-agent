namespace eThangAgent.ToolDomain;

/// <summary>One line-range read: the delivered slice plus the file's shape at the
///     read moment. <paramref name="Version"/> is null for legacy callers that do
///     not capture freshness (byte-identical legacy behavior).</summary>
public sealed record FileRead(IReadOnlyList<string> Lines, int LastLineRead, int TotalLines, FileVersion? Version = null);
