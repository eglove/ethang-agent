using System.Text.Json;
using eThangAgent.ModelDomain;

namespace eThangAgent.Transport.ACL;

/// <summary>The JSON payload of a <c>usage</c> event (issue #24): one provider call's
///     recorded spend, carrying exactly the fields a ledger row carries so the app can
///     advance its session total without re-querying.
///     <para>WHY IT TRAVELS: the loop runs in the HOST process (spec D6), so the row is
///     written there and the in-process <c>TurnCallbacks</c> path the original plan named
///     never reaches the app. The kind set is closed and strict, and app and host ship as
///     one build, so extending it on both sides needs no negotiation.</para>
///     <para>Every member is DEFAULTED on the record: a host of an older build omits them,
///     and the app then reads the omission as unreported rather than as zero. A null price
///     means the price was unknown at write time — the row contributes tokens and no cost,
///     never a fabricated one.</para>
///     <para>This is a NEUTRAL wire shape (primitives only), not the domain's
///     <see cref="UsageRow"/>: transport speaks its own vocabulary and the two sides
///     convert through <see cref="From"/> / <see cref="ToRow"/>, the one writer beside the
///     one reader, so the two cannot drift.</para></summary>
public sealed record RootTurnUsagePayload(
    string? CallClass = null,
    string? ModelId = null,
    string? Provider = null,
    string? WorkspaceId = null,
    int InputTokens = 0,
    int OutputTokens = 0,
    int? CachedInputTokens = null,
    int? ReasoningTokens = null,
    decimal? PromptPricePerToken = null,
    decimal? PromptCacheReadPricePerToken = null,
    decimal? CompletionPricePerToken = null,
    string? RequestedModelId = null,
    string? ServedModelId = null,
    long? DurationMs = null,
    long? FirstTokenMs = null,
    string? ServedProvider = null,
    decimal? ReportedCost = null,
    bool? ExpectsReportedCost = null)
{
  /// <summary>Projects one recorded row onto its wire form — the host's emission shape.</summary>
  public static RootTurnUsagePayload From(UsageRow row)
  {
    ArgumentNullException.ThrowIfNull(row);
    return new RootTurnUsagePayload(row.CallClass, row.ModelId, row.Provider, row.WorkspaceId,
        row.InputTokens, row.OutputTokens, row.CachedInputTokens, row.ReasoningTokens,
        row.PromptPricePerToken, row.PromptCacheReadPricePerToken, row.CompletionPricePerToken,
        row.RequestedModelId, row.ServedModelId, row.DurationMs, row.FirstTokenMs, row.ServedProvider,
        row.ReportedCost, row.ExpectsReportedCost);
  }

  /// <summary>Rebuilds the row the status bar's tally consumes. A payload that omits the
  ///     class reads as <c>main_loop</c> — the same legacy rule the stored rows follow.</summary>
  public UsageRow ToRow() => new(CallClass ?? MainLoopClass, ModelId, Provider, WorkspaceId,
      InputTokens, OutputTokens, CachedInputTokens, ReasoningTokens,
      PromptPricePerToken, PromptCacheReadPricePerToken, CompletionPricePerToken,
      RequestedModelId, ServedModelId, DurationMs, FirstTokenMs, ServedProvider, ReportedCost, ExpectsReportedCost);

  /// <summary>Serializes one recorded row for a <c>usage</c> event.</summary>
  public static string Serialize(UsageRow row) => JsonSerializer.Serialize(From(row));

  /// <summary>Parses one JSON usage payload, or null when it does not decode. The one
  ///     reader of the shape: the host writes it through <see cref="Serialize"/>, the app
  ///     (and tests) read it here.</summary>
  public static RootTurnUsagePayload? Parse(string payload)
  {
    ArgumentNullException.ThrowIfNull(payload);
    try
    {
      return JsonSerializer.Deserialize<RootTurnUsagePayload>(payload);
    }
    catch (JsonException)
    {
      return null;
    }
  }

  /// <summary>The persisted wire name of an ordinary loop call, repeated here so the app
  ///     can apply the legacy null-class rule without referencing the Agent Domain's closed
  ///     set from a wire reader.</summary>
  private const string MainLoopClass = "main_loop";
}
