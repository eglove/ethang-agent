namespace eThangAgent.ToolDomain;

/// <summary>Enforces the per-call <see cref="ToolTimeout"/> budget around one execution.
///     The budget token is linked with the caller's: caller cancellation still wins, and
///     an elapsed budget surfaces as the standard <c>Error [ToolTimeout]</c> result —
///     never an exception escaping to the loop.</summary>
public static class ToolExecution
{
  public static Task<ToolResult> RunAsync(
      string toolName, TimeSpan timeout,
      Func<CancellationToken, Task<ToolResult>> execute,
      CancellationToken ct) =>
      RunAsync(toolName, timeout, execute, progress: null, ct);

  /// <summary>Enforces the budget around a run that publishes its printed lines as it goes.
  ///     <paramref name="progress"/> is how an elapsed budget still reports them: the run is
  ///     abandoned where it stands, and what it had already printed is the only account of
  ///     how far it got.</summary>
  public static async Task<ToolResult> RunAsync(
      string toolName, TimeSpan timeout,
      Func<CancellationToken, Task<ToolResult>> execute,
      ExecOutputProgress? progress, CancellationToken ct)
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
      return ToolTimeout.TimedOut(toolName, timeout, progress);
    }
  }
}
