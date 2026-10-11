using eThangAgent.ModelDomain;

namespace eThangAgent.Composition;

/// <summary>Session-end flush of the measured-mix prior (spec #55 section 4). The factory
///     resolves this singleton so the container tracks it for disposal; when the session's
///     container is disposed (the Desktop's normal tab close, or any host teardown), the
///     accumulated totals are written to the workspace prior. The carrier is read at
///     DISPOSAL time — the factory fills it after the container builds — and a zero-turn
///     policy writes nothing (an error-path container that never ran must not stamp a
///     prior). The writer swallows store faults and the catch-all here keeps a flush fault
///     from ever breaking disposal.</summary>
public sealed class MeasuredMixSessionFlush(MeasuredMixCarrier carrier) : IAsyncDisposable
{
  private readonly MeasuredMixCarrier _carrier = carrier ?? throw new ArgumentNullException(nameof(carrier));

  public async ValueTask DisposeAsync()
  {
    MeasuredMixPolicy? policy = _carrier.Policy;
    MeasuredMixPriorWriter? writer = _carrier.Writer;
    if (policy is null || writer is null)
    {
      return;
    }

    (long totalInput, long totalOutput, int turns) = policy.Totals;
    if (turns == 0)
    {
      return;
    }

    // Best-effort: the writer swallows store faults; the catch-all keeps a flush fault
    // from ever breaking container disposal.
#pragma warning disable CA1031 // Do not catch general exception types — best-effort flush per spec #55 section 4.
    try
    {
      await writer.WriteAsync(new MeasuredMixPrior(totalInput, totalOutput, turns, DateTimeOffset.UtcNow))
          .ConfigureAwait(false);
    }
    catch
    {
      // Swallowed deliberately: a prior flush must never break disposal.
    }
#pragma warning restore CA1031
  }
}
