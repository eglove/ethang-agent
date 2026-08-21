namespace eThangAgent.ToolDomain;

public sealed record ToolDefinition(string Name, string Description, IReadOnlyList<ToolParameter> Parameters);
