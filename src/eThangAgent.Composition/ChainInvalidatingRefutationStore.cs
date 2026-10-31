using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition;

/// <summary>The refutation store, with the chain-step invalidation D5 requires (issue #304).
///     <para>WHY A DECORATOR ON THIS PORT: D5 says the marker is set BY THE FALSIFICATION, not
///     by a human and not by an agent - so the write has to happen where a refutation is
///     written, and it has to be automatic. <see cref="IRefutationStore"/> is that one site
///     (the experiment tool's refuted-verdict path), so decorating it is what makes the rule
///     structural instead of a second call site somebody can forget.</para>
///     <para>WHICH STEP: the nearest PRECEDING finished step of the session that recorded the
///     refutation - the same rule <c>Chain.FixTargetFor</c> already states for #320's deny
///     back-edge, and for the same reason: chain steps are independent (#299 D8), so the
///     record carries no link from a finding to the step it disproves. THE LIMIT IS STATED
///     RATHER THAN PAPERED OVER: the ledger is a flat append-only list with no per-record step
///     attribution, so the harness can name the work a refutation came AFTER but cannot name
///     the exact step that concluded the disproved thing. A per-record attribution would be a
///     second record of what the chain already says, which is the duplication #310 retired.
///     </para>
///     <para>A REFUTATION THAT LANDS NOWHERE IS NOT AN ERROR: an ordinary session records
///     refutations too (the workspace-visible half of #315 is for every session), and one that
///     belongs to no chain step simply shares the finding and invalidates nothing.</para>
///     <para>THE REFUTATION IS WRITTEN FIRST AND THE MARKER SECOND, the ledger-before-share
///     order the experiment tool already follows: a marker without a refutation would refuse a
///     chain's completion naming a record that does not exist.</para></summary>
internal sealed class ChainInvalidatingRefutationStore(StateServiceRefutationStore inner,
    Func<ChainService?> chains, Func<string?> sessionId, TimeProvider clock) : IRefutationStore
{
  private readonly StateServiceRefutationStore _inner =
      inner ?? throw new ArgumentNullException(nameof(inner));
  private readonly Func<ChainService?> _chains = chains ?? throw new ArgumentNullException(nameof(chains));
  private readonly Func<string?> _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
  private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

  /// <summary>Writes the refutation, then marks the step it disproves.</summary>
  /// <param name="key">The refutation's state key.</param>
  /// <param name="value">The rendered refutation record.</param>
  /// <param name="ct">Cancellation.</param>
  public async Task<Result<int>> WriteAsync(string key, string value, CancellationToken ct = default)
  {
    Result<int> written = await _inner.WriteAsync(key, value, ct).ConfigureAwait(false);
    return written.IsSuccess
        ? await InvalidateAsync(key, written.Value, ct).ConfigureAwait(false)
        : written;
  }

  /// <summary>Marks the step, best effort by contract: the refutation is already written and
  ///     the finding stands whatever happens here. A marker that could not be written is
  ///     REPORTED rather than swallowed - a chain that completes on a conclusion its own ledger
  ///     disproved is the failure D5 exists to prevent, and silence is how it would happen.
  ///     </summary>
  private async Task<Result<int>> InvalidateAsync(string key, int version, CancellationToken ct)
  {
    if (_chains() is not { } chainService || _sessionId() is not { } session)
    {
      return Result.Success(version);
    }

    Result<Chain?> found = await chainService.FindBySessionAsync(session, ct).ConfigureAwait(false);
    return !found.IsSuccess
        ? Result.Failure<int>(found.Error)
        : await MarkFoundAsync(chainService, found.Value, session, key, version, ct).ConfigureAwait(false);
  }

  /// <summary>Marks the step for the chain the session holds, or reports that it holds none.
  ///     Extracted so the store failure and the no-chain case stay separate statements (S3358).
  ///     </summary>
  private async Task<Result<int>> MarkFoundAsync(ChainService chainService, Chain? found,
      string session, string key, int version, CancellationToken ct) =>
      found is { } chain && chain.StepAt(PositionOf(chain, session)) is { } step
          ? await MarkAsync(chainService, chain, step, key, version, ct).ConfigureAwait(false)
          : Result.Success(version);

  /// <summary>The position the session holds. The record is the source, never the caller's
  ///     memory: a resumed or restarted launch has only the row.</summary>
  private static int PositionOf(Chain chain, string session) =>
      chain.Steps.First(step => string.Equals(step.SessionId, session, StringComparison.Ordinal)).Position;

  /// <summary>Marks the step a refutation is about: its own when it is the chain's first, else
  ///     the nearest preceding finished step (the <c>FixTargetFor</c> rule).</summary>
  private async Task<Result<int>> MarkAsync(ChainService chains, Chain chain, ChainStep step,
      string key, int version, CancellationToken ct)
  {
    if (chain.FixTargetFor(step.Position) is not { } target)
    {
      // Nothing finished before this step: there is no earlier conclusion to disprove, so the
      // refutation shares a finding and invalidates nothing.
      return Result.Success(version);
    }

    Result<Chain> marked = await chains
        .InvalidateStepAsync(chain.Id, target.Position, key, _clock.GetUtcNow(), ct)
        .ConfigureAwait(false);
    return marked.IsSuccess ? Result.Success(version) : Result.Failure<int>(marked.Error);
  }
}
