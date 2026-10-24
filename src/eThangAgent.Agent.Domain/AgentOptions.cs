using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain;

/// <summary>Optional construction knobs for <see cref="Agent"/>. Absent members behave
/// exactly as the former absent parameters did: no system-prompt provider, a freshly
/// generated id, depth 0, and the default auto-continuation cap.</summary>
public sealed record AgentOptions
{
  public ISystemPromptProvider? SystemPrompt { get; init; }

  /// <summary>Identity of the agent. Roots leave it null to generate one; spawned children carry their persisted id.</summary>
  public AgentId? Id { get; init; }

  /// <summary>Depth in the spawn tree; roots are depth 0.</summary>
  public int Depth { get; init; }

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
  public IChildTranscriptStore? TranscriptSink { get; init; }

  /// <summary>Invoked with "content" or "reasoning" on every provider stream fragment
  ///     (the stream-stall liveness signal). May fire on arbitrary threads; observers
  ///     must be thread-safe and fast. Null (legacy wiring) stamps nothing:
  ///     byte-identical legacy behavior.</summary>
  public Action<string>? OnStreamActivity { get; init; }
  /// <summary>Durable session identity stamped onto every provider request this loop
  ///     builds (OpenRouter sticky sessions / prompt caching). Null (legacy wiring)
  ///     leaves the request's id unset: byte-identical legacy behavior.</summary>
  public string? SessionId { get; init; }

  /// <summary>Persists the artifact an image-generation server tool produced inside the
  ///     provider's response (issue #230). The loop saves the bytes into the workspace and
  ///     names the path in the surfaced line, so the artifact is reachable by the user and
  ///     by a later read_image. Null (legacy wiring) surfaces the call without persisting
  ///     anything: byte-identical legacy behavior.</summary>
  public IServerToolImageStore? ServerToolImages { get; init; }

  /// <summary>Append-only event log the loop records reasoning traces to (issue #263):
  ///     the TELEMETRY channel, never the context channel — no request builder reads it
  ///     back, so a persisted trace changes no request byte. Null (legacy wiring) records
  ///     nothing: byte-identical legacy behavior.</summary>
  public IAgentEventLog? EventLog { get; init; }
}
