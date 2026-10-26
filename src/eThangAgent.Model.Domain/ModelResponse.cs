namespace eThangAgent.ModelDomain;

/// <summary>A completed model response: assistant text, any tool calls, and why the
///     response ended. FinishReason defaults to <see cref="FinishReason.Stop"/> — the
///     named-leniency default so simple fakes and legacy callers mean "completed
///     normally"; real provider ACLs set it explicitly from the wire format.</summary>
/// <param name="Content">Assistant text, or null when the model returned only tool calls.</param>
/// <param name="ToolCalls">Tool calls requested by the model; empty for a plain answer.</param>
/// <param name="FinishReason">Why the response ended.</param>
/// <param name="Usage">Provider-reported token usage, or null when the provider reported none.</param>
/// <param name="ServerToolCalls">Server-side tool calls the provider executed inside this
/// response (web_search etc.); empty when none. They never enter the message history —
/// hosts surface them from here.</param>
public sealed record ModelResponse(
    string? Content,
    IReadOnlyList<ToolCallRequest> ToolCalls,
    FinishReason FinishReason = FinishReason.Stop,
    TokenUsage? Usage = null,
    IReadOnlyList<ServerToolCall>? ServerToolCalls = null)
{
  /// <summary>Never null: an absent list reads as empty.</summary>
  public IReadOnlyList<ServerToolCall> ServerToolCalls { get; init; } = ServerToolCalls ?? [];

  /// <summary>The reasoning text the provider streamed for this response, accumulated
  ///     across fragments — or null when the provider emitted none (issue #263).
  ///     <para>This is the TELEMETRY channel, never the context channel: nothing in the
  ///     request builder reads it, so a response carrying reasoning produces a
  ///     byte-identical next request. A provider that streams only a summary
  ///     (<c>response.reasoning_summary_text.delta</c>) accumulates the summary here; the
  ///     raw trace exists only when the provider streams one.</para></summary>
  public string? Reasoning { get; init; }

  /// <summary>The model the provider reported as having served this response, read from
  ///     the wire's <c>model</c> field — or null when the provider named none (issue #290).
  ///     <para>This is a DIFFERENT FACT from the requested model the request carried. For a
  ///     fixed model the two strings are equal and the distinction is invisible; for a
  ///     router (<c>openrouter/free</c> selects per request) they diverge, and only this
  ///     field can say what actually ran and what its prices are.</para>
  ///     <para>NEVER FABRICATED: an unreported serving model stays null rather than being
  ///     coerced to the requested id. "The provider did not say" is a real answer, and
  ///     collapsing it into the request would restore exactly the confident-wrong record
  ///     this field exists to eliminate.</para></summary>
  public string? ServedModelId { get; init; }
}
