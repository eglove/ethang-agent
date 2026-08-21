namespace eThangAgent.ToolDomain;

public sealed record FileRead(IReadOnlyList<string> Lines, int LastLineRead, int TotalLines);
