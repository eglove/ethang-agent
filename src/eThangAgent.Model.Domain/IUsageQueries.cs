using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

/// <summary>The usage ledger's READ side (issue #24): rollups over the persisted per-call
///     usage rows. A domain-owned seam implemented by the Storage ACL over
///     <c>agent_events</c> — the read path is CQRS, so no domain logic runs here and the
///     implementation is free to use SQL.
///     <para>SESSION ATTRIBUTION IS A WALK, NOT A STAMP (B11): a child agent stamps its
///     OWN id as its session id, so filtering <c>session_id = root</c> would omit every
///     subagent's spend. The implementation collects the subtree by walking
///     <c>agents.parent_id</c> from the root and joins the usage rows onto it, which is
///     what makes a child's spend count toward the session that caused it — and what
///     makes the rollup work retroactively for rows written before it existed.</para>
///     <para>Every member is a QUERY: no side effects, no writes, and reads run on the
///     read-only connection so a rollup can never mutate the ledger it measures.</para></summary>
public interface IUsageQueries
{
  /// <summary>The rolled-up spend of one session and every agent it spawned. An unknown
  ///     session id is an EMPTY summary, not a failure: a session that has not run a turn
  ///     yet has no rows, and the status bar must render that state rather than an
  ///     error.</summary>
  Task<Result<UsageSummary>> SessionSummaryAsync(string sessionId, CancellationToken ct = default);

  /// <summary>Observed per-provider performance from the ledger (issue #295): median call
  ///     duration, median first-token latency, median output rate, and the token counts the
  ///     cache-hit rate derives from — per model/provider pair, each with its sample count.
  ///     <para>WHY THIS IS THE HONEST SOURCE (D3): the catalog's own latency and throughput
  ///     fields are structurally dead — measured live, null on all 52 endpoints across 5
  ///     models — so our own recorded call durations are the only measurement of the provider
  ///     we actually reached. They come from the same rows this interface already rolls up.</para>
  ///     <para>THE FALLBACK CHAIN IS SESSION, THEN WORKSPACE, THEN GLOBAL (D5), and the answer
  ///     names which rung answered. A fresh session has no rows of its own, so refusing to
  ///     answer would leave the picker with nothing to project from; answering silently from a
  ///     wider scope would present another session's history as this one's.</para>
  ///     <para>An empty scope is an EMPTY REPORT, not a failure — the same rule
  ///     <see cref="SessionSummaryAsync"/> follows, and for the same reason.</para></summary>
  Task<Result<ProviderPerformanceReport>> ProviderPerformanceAsync(
      ProviderPerformanceRequest request, CancellationToken ct = default);
}
