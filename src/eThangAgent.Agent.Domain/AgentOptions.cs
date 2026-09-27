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

  /// <summary>Runs compaction at the utilization threshold. Null means the trigger
  ///     never fires.</summary>
  public IContextCompactor? ContextCompactor { get; init; }

  /// <summary>Utilization percent that trips the compactor. Must lie in (0, 100].</summary>
  public double CompactionThreshold { get; init; } = Agent.DefaultCompactionThreshold;

  /// <summary>Receives liveness beats at loop safe points (iteration top, tool-call
  ///     boundaries). Null (legacy wiring) means the loop never beats: byte-identical
  ///     legacy behavior.</summary>
  public IAgentHeartbeat? Heartbeat { get; init; }

  /// <summary>Event stream the loop publishes progress to at the same safe points.
  ///     Null (legacy wiring) publishes nothing: byte-identical legacy behavior.</summary>
  public IAgentEvents? Events { get; init; }

  /// <summary>Durable session identity stamped onto every provider request this loop
  ///     builds (OpenRouter sticky sessions / prompt caching). Null (legacy wiring)
  ///     leaves the request's id unset: byte-identical legacy behavior.</summary>
  public string? SessionId { get; init; }

  /// <summary>Archive store for oversized tool results (the store-and-read-back
  ///     context policy). Null (legacy wiring) means tool results enter history in
  ///     full: byte-identical legacy behavior.</summary>
  public IToolOutputArchive? ToolOutputArchive { get; init; }

  /// <summary>Tool results longer than this many characters are archived in full and
  ///     enter history as a head-and-tail excerpt naming the read-back handle.
  ///     Roughly Strands' 1,500-token threshold. Only read when
  ///     <see cref="ToolOutputArchive"/> is wired.</summary>
  public int ToolResultArchiveThreshold { get; init; } = Agent.DefaultToolResultArchiveThreshold;

  /// <summary>Characters of head kept in an archived result's excerpt. Error results
  ///     keep only the head: the leading <c>Error [Code]: ...</c> line carries the
  ///     fault.</summary>
  public int ToolResultExcerptHeadChars { get; init; } = Agent.DefaultToolResultExcerptHeadChars;

  /// <summary>Characters of tail kept in a successful result's excerpt (the tail
  ///     often carries the outcome line). Zero for error results.</summary>
  public int ToolResultExcerptTailChars { get; init; } = Agent.DefaultToolResultExcerptTailChars;
}
