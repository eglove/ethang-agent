namespace eThangAgent.ToolDomain;

public sealed record ToolParameter(string Name, ToolParameterType Type, string Description, int? Minimum = null);
