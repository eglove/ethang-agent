using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.TestSupport;

/// <summary>Builds a chain step in the state a test needs, so no test re-derives that state
///     by simulating the passage of time.
///     <para>WHY THIS EXISTS: a claim has no time limit, so 'a step with three attempts' is
///     not reachable by advancing a clock - it is reached by claiming and releasing three
///     times, which is what a launch that never ran a session does. Spelling that loop out in
///     each test made every one of them a place to get the rule wrong, and they all had to be
///     rewritten when the claim rule changed.</para>
///     <para>LINKED SOURCE, not a project reference: the domain and application suites target
///     plain net10.0 and the support projects do not, so this file is compiled into each of
///     them instead of dragging a framework along.</para></summary>
internal static class ChainAttempts
{
  /// <summary>The chain with the step at <paramref name="position"/> claimed
  ///     <paramref name="attempts"/> times, the last claim still held by
  ///     <paramref name="sessionId"/>. Every earlier attempt is released, because a claim
  ///     that is still held cannot be claimed again.</summary>
  /// <param name="chain">The chain to build on.</param>
  /// <param name="position">The 1-based step position.</param>
  /// <param name="sessionId">The session that holds the final claim.</param>
  /// <param name="attempts">How many attempts to record; at least one.</param>
  /// <param name="at">The clock every claim starts at.</param>
  public static Chain WithAttempts(Chain chain, int position, string sessionId, int attempts,
      DateTimeOffset at)
  {
    ArgumentNullException.ThrowIfNull(chain);
    ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
    ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);

    Chain current = chain;
    for (int attempt = 1; attempt < attempts; attempt++)
    {
      current = current.ClaimStep(position, sessionId, at)
          .ReleaseStep(position, ChainActor.Harness, at);
    }

    return current.ClaimStep(position, sessionId, at);
  }

  /// <summary>The same state built through <see cref="ChainService"/>, for tests that must
  ///     exercise the service's own claim path rather than the aggregate's.</summary>
  /// <param name="chains">The service to claim through.</param>
  /// <param name="chainId">The chain to build on.</param>
  /// <param name="position">The 1-based step position.</param>
  /// <param name="sessionId">The session that holds the final claim.</param>
  /// <param name="attempts">How many attempts to record; at least one.</param>
  /// <param name="at">The clock every claim starts at.</param>
  /// <param name="ct">Cancellation.</param>
  public static async Task<Chain> WithAttemptsAsync(ChainService chains, int chainId, int position,
      string sessionId, int attempts, DateTimeOffset at, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(chains);
    ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
    ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);

    Chain current = await ClaimAsync(chains, chainId, position, sessionId, at, ct).ConfigureAwait(false);
    for (int attempt = 1; attempt < attempts; attempt++)
    {
      await ReleaseAsync(chains, chainId, position, at, ct).ConfigureAwait(false);
      current = await ClaimAsync(chains, chainId, position, sessionId, at, ct).ConfigureAwait(false);
    }

    return current;
  }

  private static async Task<Chain> ClaimAsync(ChainService chains, int chainId, int position,
      string sessionId, DateTimeOffset at, CancellationToken ct)
  {
    Result<Chain> claimed = await chains.ClaimStepAsync(chainId, position, sessionId, at, ct)
        .ConfigureAwait(false);
    return claimed.IsSuccess
        ? claimed.Value
        : throw new InvalidOperationException(
            $"claiming chain #{chainId} step {position} failed: {claimed.Error.Message}");
  }

  private static async Task ReleaseAsync(ChainService chains, int chainId, int position,
      DateTimeOffset at, CancellationToken ct)
  {
    Result<Chain> released = await chains.ReleaseStepAsync(chainId, position, ChainActor.Harness, at, ct)
        .ConfigureAwait(false);
    if (!released.IsSuccess)
    {
      throw new InvalidOperationException(
          $"releasing chain #{chainId} step {position} failed: {released.Error.Message}");
    }
  }
}
