namespace eThangAgent.ToolDomain;

/// <summary>Enforces the per-call <see cref="ToolTimeout"/> budget around one execution.
///     The budget token is linked with the caller's: caller cancellation still wins, and
///     an elapsed budget surfaces as the standard <c>Error [ToolTimeout]</c> result —
///     never an exception escaping to the loop.</summary>
public static class ToolExecution
{
  public static async Task<ToolResult> RunAsync(
      string toolName, TimeSpan timeout,
      Func<CancellationToken, Task<ToolResult>> execute,
      CancellationToken ct)
  {
    ArgumentNullException.ThrowIfNull(execute);
    using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    cts.CancelAfter(timeout);
    try
    {
      return await execute(cts.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
    {
      return ToolTimeout.TimedOut(toolName, timeout);
    }
  }
}
