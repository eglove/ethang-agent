using eThangAgent.ModelDomain;

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
}
