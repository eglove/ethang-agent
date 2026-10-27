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

  /// <summary>The provider the wire reported as having served this response, or null when it
  ///     named none (issue #292).
  ///     <para>MEASURED FACT, 2026-10-27: the Responses API's response object does NOT carry a
  ///     provider field — six live models (including <c>openrouter/auto</c> and
  ///     <c>openrouter/free</c>, which resolve to a concrete model), pinned and unpinned,
  ///     streaming and not, all returned the same key set with no <c>provider</c> member, and
  ///     the <c>X-Provider-Name</c> header the CORS expose-list advertises was absent on this
  ///     surface. So this member is null in practice today, and null is a REAL ANSWER: the
  ///     upstream vendor is simply not something this wire tells us. It is parsed anyway
  ///     because a provider that DOES name one must be recorded rather than guessed at — the
  ///     same forward-compatible rule <see cref="ServedModelId"/> follows.</para>
  ///     <para>NEVER FABRICATED FROM THE REQUEST: a session that pinned a provider carries
  ///     that pin in its <c>ModelConfig</c>, and copying it here would turn a request's
  ///     intention into a claim about what served — issue #292's B3 forbids exactly that.</para></summary>
  public string? ServedProvider { get; init; }

  /// <summary>How long the provider call took, in whole milliseconds, measured around the
  ///     call itself — request dispatch to response settled — or null when nothing measured
  ///     it (issue #292).
  ///     <para>THE PROVIDER CALL, NOT THE ITERATION: an iteration includes tool dispatch and
  ///     local work, so timing it would compare providers on a number that is mostly ours.
  ///     A provider ACL owns dispatch and settle, so it is the only layer that can measure
  ///     this honestly.</para>
  ///     <para>NULL IS 'NOT MEASURED' AND ZERO IS A REAL MEASUREMENT: a call that completed in
  ///     under a millisecond reports 0, which is a fact; a response nobody timed reports null,
  ///     which is the absence of one. Coercing either into the other would make a fast
  ///     provider and an unmeasured one indistinguishable in exactly the comparison this
  ///     member exists to serve.</para></summary>
  public long? DurationMs { get; init; }

  /// <summary>How long the provider took to send its first streamed delta, in whole
  ///     milliseconds, or null when it streamed none (issue #292).
  ///     <para>A DIFFERENT FACT FROM <see cref="DurationMs"/>: a provider that sends its first
  ///     token in 300 ms and finishes in 40 s feels nothing like one that thinks for 20 s and
  ///     then dumps everything, and only the pair can tell them apart.</para>
  ///     <para>NULL, NEVER ZERO, FOR A NON-STREAMING RESPONSE: a single JSON body has no first
  ///     delta, so there is nothing to time. A fabricated 0 would read as instant
  ///     responsiveness — the opposite of the truth.</para></summary>
  public long? FirstTokenMs { get; init; }

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
