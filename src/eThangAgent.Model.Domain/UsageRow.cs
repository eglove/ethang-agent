namespace eThangAgent.ModelDomain;

/// <summary>One persisted usage row, as the READ MODEL sees it (issue #24): the token
///     counts a provider scored, the class of call that produced them, the model whose
///     prices applied, and the three prices stamped at write time.
///     <para>WHY THIS SHAPE EXISTS: the write side's record lives in the Agent Domain
///     (<c>AgentUsageRecorded</c>) and the Model Domain cannot reference it, so the read
///     model speaks this neutral row instead. The Storage ACL maps stored payloads onto
///     it; the loop maps a live call onto it for the status bar's in-turn increment.
///     One shape, one cost formula.</para>
///     <para><see cref="CallClass"/> is the persisted wire name (<c>main_loop</c> or
///     <c>compaction</c>) and is OPAQUE here: the closed set is owned by the Agent Domain,
///     and the legacy rule (a null class reads as <c>main_loop</c>) is applied where the
///     domain type is available, never re-derived here.</para>
///     <para>Every price is nullable and a null price means UNKNOWN — never free. A row
///     whose prices are all null contributes tokens but no cost, and the summary counts it
///     as unpriced rather than pricing it at zero.</para></summary>
/// <param name="CallClass">The persisted call class (opaque to this domain).</param>
/// <param name="ModelId">The model whose prices applied — the SERVED model when the
///     provider named one, the requested model only as the fallback (issue #290). Null when
///     the call cannot name its model. This is the pricing basis and the per-model rollup's
///     grouping key.</param>
/// <param name="Provider">The provider whose price was stamped, or null when unnamed.</param>
/// <param name="WorkspaceId">The workspace the work belongs to, or null when unwired.</param>
/// <param name="InputTokens">Provider-scored prompt tokens, cached share included.</param>
/// <param name="OutputTokens">Provider-scored completion tokens.</param>
/// <param name="CachedInputTokens">The prompt-cache hit the provider reported, or null.</param>
/// <param name="ReasoningTokens">The reasoning share of the output, or null.</param>
/// <param name="PromptPricePerToken">The uncached-input price stamped at write time, or null.</param>
/// <param name="PromptCacheReadPricePerToken">The cache-read price stamped at write time, or null.</param>
/// <param name="CompletionPricePerToken">The output price stamped at write time, or null.</param>
/// <param name="RequestedModelId">The model the session asked for, or null when unnamed
///     (issue #290). A router makes this differ from <paramref name="ModelId"/>.</param>
/// <param name="ServedModelId">The model the provider reported as having served the call,
///     or null when it reported none. Null means the row was priced on the requested model —
///     the fallback, visible rather than assumed.</param>
/// <param name="DurationMs">How long the provider call took in whole milliseconds, or null
///     when it was not measured (issue #292). Null is 'not measured', never 'instant' — a
///     call that finished inside a millisecond carries 0, which is a real measurement.</param>
/// <param name="FirstTokenMs">How long the provider took to send its first streamed delta in
///     whole milliseconds, or null when it streamed none (issue #292) — always null for a
///     non-streaming call, which has no first delta to time.</param>
/// <param name="ServedProvider">The provider the wire reported as having served the call, or
///     null when it named none (issue #292). Distinct from <paramref name="Provider"/>, which
///     is the provider whose PRICES were stamped: on this surface the wire names no serving
///     provider, so this is usually null while the priced provider is known.</param>
public sealed record UsageRow(
    string CallClass,
    string? ModelId,
    string? Provider,
    string? WorkspaceId,
    int InputTokens,
    int OutputTokens,
    int? CachedInputTokens,
    int? ReasoningTokens,
    decimal? PromptPricePerToken,
    decimal? PromptCacheReadPricePerToken,
    decimal? CompletionPricePerToken,
    string? RequestedModelId = null,
    string? ServedModelId = null,
    long? DurationMs = null,
    long? FirstTokenMs = null,
    string? ServedProvider = null)
{
  /// <summary>True when this row's prices belong to the REQUESTED model because the
  ///     provider named no serving model (issue #290): the fallback, stated rather than
  ///     assumed. False when the served model priced the row, and false when no model is
  ///     named at all — an unpriced row is not a fallback.</summary>
  public bool PricedOnRequestedModel => ServedModelId is null && ModelId is not null;
}

/// <summary>Token and cost totals for one group of usage rows (one call class, or one
///     model). <see cref="Cost"/> is null when no row in the group could be priced, so a
///     group that was never priced never reads as free.</summary>
public sealed record UsageTotal(
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    long ReasoningTokens,
    decimal? Cost,
    int PricedCalls,
    int UnpricedCalls)
{
  /// <summary>Prompt plus completion tokens: what the group cost in throughput terms.</summary>
  public long TotalTokens => InputTokens + OutputTokens;

  /// <summary>Every call the group counted, priced or not.</summary>
  public int Calls => PricedCalls + UnpricedCalls;
}

/// <summary>One call class's totals (<c>main_loop</c>, <c>compaction</c>).</summary>
public sealed record UsageClassTotal(string CallClass, UsageTotal Totals);

/// <summary>One model's totals. <see cref="ModelId"/> is null for rows whose model could
///     not be named (an orphaned agent row, a legacy payload).</summary>
public sealed record UsageModelTotal(string? ModelId, UsageTotal Totals);

/// <summary>The rolled-up spend of one session and every agent it spawned (issue #24,
///     B11): token totals, per-call-class totals, per-model totals, and cost.
///     <para>ATTRIBUTION IS A WALK, NOT A STAMP: children stamp their OWN id as their
///     session id, so a naive <c>session_id = root</c> filter would omit every subagent's
///     spend. The Storage ACL collects the subtree by walking <c>agents.parent_id</c> from
///     the root, which is what makes a child's rows count toward the session that caused
///     them — and what makes the rollup work retroactively for rows already written.</para>
///     <para><c>Totals.Cost</c> is the sum over PRICED rows only and is null when nothing
///     in scope could be priced. <c>Totals.UnpricedCalls</c> states how much of the spend
///     the number does not cover, so a partial cost is never silently presented as the
///     whole bill (B12).</para></summary>
public sealed record UsageSummary(
    UsageTotal Totals,
    IReadOnlyList<UsageClassTotal> ByClass,
    IReadOnlyList<UsageModelTotal> ByModel)
{
  /// <summary>The summary of a session with no recorded spend.</summary>
  public static UsageSummary Empty { get; } = new(
      new UsageTotal(0, 0, 0, 0, null, 0, 0), [], []);

  /// <summary>True when any row was counted — the status line renders nothing otherwise.</summary>
  public bool HasRows => Totals.Calls > 0;

  /// <summary>True when at least one row could be priced (the cost segment is shown).</summary>
  public bool HasPricedSpend => Totals.PricedCalls > 0;
}
