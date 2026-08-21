namespace eThangAgent.ToolDomain;

public sealed class NullExecActivitySink : IExecActivitySink
{
    public static readonly NullExecActivitySink Instance = new();

    private NullExecActivitySink() { }

    public Task RecordAsync(ExecActivity activity, CancellationToken ct = default)
        => Task.CompletedTask;
}
