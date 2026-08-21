namespace eThangAgent.ToolDomain;

public interface IExecOutputStore
{
    Task<string> WriteAsync(string content, CancellationToken ct = default);
}
