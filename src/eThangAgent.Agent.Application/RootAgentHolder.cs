using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;
using Ag = eThangAgent.AgentDomain.Agent;

namespace eThangAgent.Agent.Application;

/// <summary>Holds the root agent for a session and rebuilds it when its model changes.
///     The root agent is constructed lazily on the first message (see
///     <see cref="RootAgentResolver"/>): when no explicit model is configured, the model is
///     not known until the first prompt is classified. Once built, the agent is rebuilt on
///     every model reselection (the cadence boundary), preserving the shared
///     <see cref="Conversation"/> — whose message history IS the agent's state — the
///     provider, tool registry, and system-prompt provider. <see cref="Agent"/> itself stays
///     immutable; mid-session model change is realized by constructing a fresh wrapper over
///     the same shared dependencies rather than mutating <c>Config</c>.</summary>
public sealed class RootAgentHolder(
    IModelProvider provider,
    Conversation conversation,
    IToolRegistry tools,
    ISystemPromptProvider? systemPrompt = null,
    int? maxAutoContinuations = null)
{
  private readonly IModelProvider _provider = provider ?? throw new ArgumentNullException(nameof(provider));
  private readonly Conversation _conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
  private readonly IToolRegistry _tools = tools ?? throw new ArgumentNullException(nameof(tools));

  /// <summary>The model currently serving the root, or null before the first turn resolves it.</summary>
  public ModelConfig? CurrentConfig { get; private set; }

  /// <summary>The current root agent, or null before the first turn builds it.</summary>
  public Ag? Current { get; private set; }

  /// <summary>Builds (or rebuilds) the root agent with <paramref name="config"/>. The shared
  ///     conversation and dependencies are reused, so a rebuild preserves all message history
  ///     and in-flight state. Idempotent when <paramref name="config"/> matches the current model.</summary>
  public Ag Build(Ag? existing, ModelConfig config)
  {
    ArgumentNullException.ThrowIfNull(config);
    if (existing is not null && CurrentConfig is not null && CurrentConfig == config)
    {
      return existing;
    }

    CurrentConfig = config;
    Current = new Ag(_provider, _conversation, config, _tools, systemPrompt,
        maxAutoContinuations: maxAutoContinuations ?? Ag.DefaultMaxAutoContinuations);
    return Current;
  }
}
