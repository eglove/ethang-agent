namespace eThangAgent.ToolDomain;

public interface IExecActivitySink
{
  Task RecordAsync(ExecActivity activity, CancellationToken ct = default);
}
