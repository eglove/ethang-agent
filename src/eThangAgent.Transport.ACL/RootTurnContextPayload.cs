using System.Text.Json;
using eThangAgent.ModelDomain;

namespace eThangAgent.Transport.ACL;

/// <summary>The estimated per-bucket token split of the last request (the status line's
///     hover breakdown), as it travels on the wire. A separate record rather than three
///     loose nullable ints so an ABSENT breakdown (nothing reported yet) stays distinct
///     from a breakdown whose buckets are all zero — the status line renders those two
///     states differently ("" vs "System ~0 · Messages ~0 · Tools ~0").</summary>
public sealed record RootTurnContextBreakdownPayload(int? SystemPromptTokens, int? MessageTokens,
    int? ToolTokens);

/// <summary>The JSON payload of a <c>context</c> event (issue #255): the session's
///     provider-scored context accounting — the last request's input tokens, the
///     accumulated session totals, the model's catalog window, the utilization, and the
///     estimated bucket breakdown.
/// <para>WHY IT TRAVELS: the conversation's agent runs in the HOST process (spec D6), so
///     the loop's <c>OnContextUpdate</c> callback fires on the host's side. Before this
///     carrier existed the app had no event kind for it, so only the in-process path
///     (a headless/test container) ever reached <c>Status.SetContext</c> and every
///     production session showed a CTX line that never advanced.</para>
/// <para>The payload is a NEUTRAL wire shape (primitive fields only), not the domain's
///     <see cref="ContextStatus"/>: the transport speaks its own vocabulary and the two
///     sides convert through <see cref="From"/> / <see cref="ToSnapshot"/>, the one
///     writer beside the one reader, so the two cannot drift.</para></summary>
public sealed record RootTurnContextPayload(
    int? LastInputTokens,
    long TotalInputTokens,
    long TotalOutputTokens,
    int? ContextWindow,
    double? UtilizationPercent,
    RootTurnContextBreakdownPayload? Breakdown)
{
  /// <summary>Projects one domain snapshot onto its wire form — the host's emission shape.</summary>
  public static RootTurnContextPayload From(ContextSnapshot snapshot)
  {
    ArgumentNullException.ThrowIfNull(snapshot);
    ContextStatus status = snapshot.Status;
    return new RootTurnContextPayload(status.LastInputTokens, status.TotalInputTokens,
        status.TotalOutputTokens, status.ContextWindow, status.UtilizationPercent,
        snapshot.Breakdown is { } breakdown
            ? new RootTurnContextBreakdownPayload(breakdown.SystemPromptTokens,
                breakdown.MessageTokens, breakdown.ToolTokens)
            : null);
  }

  /// <summary>Rebuilds the domain snapshot the status bar consumes.</summary>
  public ContextSnapshot ToSnapshot()
      => new(new ContextStatus(LastInputTokens, TotalInputTokens, TotalOutputTokens,
              ContextWindow, UtilizationPercent),
          Breakdown is { } breakdown
              ? new ContextBreakdown(breakdown.SystemPromptTokens, breakdown.MessageTokens,
                  breakdown.ToolTokens)
              : null);

  /// <summary>Serializes one snapshot for a <c>context</c> event.</summary>
  public static string Serialize(ContextSnapshot snapshot)
      => JsonSerializer.Serialize(From(snapshot));

  /// <summary>Parses one JSON context payload, or null when it does not decode. The one
  ///     reader of the shape: the host writes it through <see cref="Serialize"/>, the app
  ///     (and tests) read it here.</summary>
  public static RootTurnContextPayload? Parse(string payload)
  {
    ArgumentNullException.ThrowIfNull(payload);
    try
    {
      return JsonSerializer.Deserialize<RootTurnContextPayload>(payload);
    }
    catch (JsonException)
    {
      return null;
    }
  }
}
