namespace eThangAgent.ToolDomain;

public sealed record ExecParseError(int Line, int Column, string Message);
