using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain;

/// <summary>Optional construction knobs for <see cref="Agent"/>. Absent members behave
/// exactly as the former absent parameters did: no system-prompt provider, a freshly
/// generated id, and the default auto-continuation cap.</summary>
public sealed record AgentOptions
{
  public ISystemPromptProvider? SystemPrompt { get; init; }

  /// <summary>Identity of the agent. Null generates a fresh one; a persisted agent supplies its stored id.</summary>
  public AgentId? Id { get; init; }

  public int MaxAutoContinuations { get; init; } = Agent.DefaultMaxAutoContinuations;

  /// <summary>Receives per-provider-call usage for context accounting. Null (legacy
  ///     wiring) means the loop runs without accounting: no reports, no updates.</summary>
  public IContextMonitor? ContextMonitor { get; init; }

  /// <summary>Runs compaction at the policy-derived ceiling. Null means the trigger
  ///     never fires.</summary>
  public IContextCompactor? ContextCompactor { get; init; }

  /// <summary>Receives liveness beats at loop safe points (iteration top, tool-call
  ///     boundaries). Null (legacy wiring) means the loop never beats: byte-identical
  ///     legacy behavior.</summary>
  public IAgentHeartbeat? Heartbeat { get; init; }

  /// <summary>Event stream the loop publishes progress to at the same safe points.
  ///     Null (legacy wiring) publishes nothing: byte-identical legacy behavior.</summary>
  public IAgentEvents? Events { get; init; }

  /// <summary>Incremental transcript persistence for child runs: every conversation
  ///     mutation flows here at the safe point it happens, so an interrupt or crash
  ///     leaves the run's history persisted. Null (legacy wiring) persists nothing
  ///     during the run: byte-identical legacy behavior.</summary>
  public IAgentTranscriptStore? TranscriptSink { get; init; }

  /// <summary>Invoked with "content" or "reasoning" on every provider stream fragment
  ///     (the stream-stall liveness signal). May fire on arbitrary threads; observers
  ///     must be thread-safe and fast. Null (legacy wiring) stamps nothing:
  ///     byte-identical legacy behavior.</summary>
  public Action<string>? OnStreamActivity { get; init; }
  /// <summary>Durable session identity stamped onto every provider request this loop
  ///     builds (OpenRouter sticky sessions / prompt caching). Null (legacy wiring)
  ///     leaves the request's id unset: byte-identical legacy behavior.</summary>
  public string? SessionId { get; init; }

  /// <summary>The session's one image store, where a produced image is persisted whether a
  ///     server-side tool emitted it or a screenshot captured it. The loop saves the bytes and
  ///     names the ABSOLUTE path in the surfaced line, so the model can read_image it on a
  ///     later turn and the user can open it. Null (legacy wiring, tests) surfaces the call
  ///     without persisting anything.</summary>
  public IImageStore? Images { get; init; }

  /// <summary>Append-only event log the loop records reasoning traces to (issue #263):
  ///     the TELEMETRY channel, never the context channel — no request builder reads it
  ///     back, so a persisted trace changes no request byte. Null (legacy wiring) records
  ///     nothing: byte-identical legacy behavior.</summary>
  public IAgentEventLog? EventLog { get; init; }

  /// <summary>Resolves the per-token prices stamped onto each usage row at write time
  ///     (issue #23). Price is stamped when the call happens because the catalog keeps no
  ///     price history - a call not priced then is unpriced forever. Null (legacy wiring)
  ///     stamps null prices: the row still records its tokens, and cost reads as null
  ///     rather than being fabricated.</summary>
  public IUsagePriceSource? UsagePrices { get; init; }

  /// <summary>The workspace this run's usage rows belong to (issue #23). One database
  ///     file serves many workspaces, so a row without it cannot be attributed to the
  ///     work that caused it. Null (legacy wiring) records no workspace: byte-identical
  ///     legacy behavior.</summary>
  public string? WorkspaceId { get; init; }
}
