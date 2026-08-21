namespace eThangAgent.ToolDomain;

public interface ITool
{
    ToolDefinition Definition { get; }
    Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default);
}
