using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;
using Ag = eThangAgent.AgentDomain.Agent;

namespace eThangAgent.Agent.Application;

/// <summary>The optional root-agent shaping collaborators, grouped: the system-prompt
///     provider, the auto-continuation budget, the context compactor, and the continuous
///     transcript sink. Null (legacy wiring) means every member falls back to the agent's
///     own defaults - including "persist nothing during the run".</summary>
public sealed record RootDependencies(
    ISystemPromptProvider? SystemPrompt = null,
    int? MaxAutoContinuations = null,
    IContextCompactor? ContextCompactor = null,
    IChildTranscriptStore? TranscriptSink = null,
    IServerToolImageStore? ServerToolImages = null,
    IAgentEventLog? EventLog = null);

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
    RootDependencies? dependencies = null,
    string? sessionId = null,
    Func<string?>? sessionIdSource = null)
{
  private readonly IModelProvider _provider = provider ?? throw new ArgumentNullException(nameof(provider));
  private readonly Conversation _conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
  private readonly IToolRegistry _tools = tools ?? throw new ArgumentNullException(nameof(tools));
  private readonly RootDependencies? _dependencies = dependencies;
  private readonly string? _sessionId = sessionId;
  private readonly Func<string?>? _sessionIdSource = sessionIdSource;

  /// <summary>The durable session identity stamped onto every provider request, resolved
  ///     at build time: the explicit id when given, else the lazily-resolved source
  ///     (the container's RootSessionIdentity, set after the container builds). Null
  ///     (legacy wiring) means no id on the request.</summary>
  private string? ResolveSessionId() => _sessionId ?? _sessionIdSource?.Invoke();

  /// <summary>The model currently serving the root, or null before the first turn resolves it.</summary>
  public ModelConfig? CurrentConfig { get; private set; }

  /// <summary>The current root agent, or null before the first turn builds it.</summary>
  public Ag? Current { get; private set; }

  /// <summary>The session identity the current agent stamps onto its provider requests
  ///     (OpenRouter sticky sessions), or null before the first build / legacy wiring.</summary>
  public string? CurrentSessionId { get; private set; }

  /// <summary>The accountant backing the current agent — context status for display
  ///     between turns. Null before the first build.</summary>
  public ContextAccountant? Accountant { get; private set; }

  /// <summary>Builds (or rebuilds) the root agent with <paramref name="config"/>. The shared
  ///     conversation and dependencies are reused, so a rebuild preserves all message history
  ///     and in-flight state. Idempotent when <paramref name="config"/> matches the current model.
  ///     <para>Spec D9: the root persists every message at the safe point it is added, exactly
  ///     as a child run does — the sink rides <see cref="AgentOptions.TranscriptSink"/> and the
  ///     agent subscribes it to the shared conversation. Because EVERY build subscribes, the
  ///     SINK retires the previous build's subscription (it registers each lease through
  ///     <c>RootTranscriptSink.TrackSubscription</c>): two live root subscriptions would push
  ///     every message twice, and a doubled turn corrupts resume. The holder does nothing
  ///     extra for this — it only hands the same sink to every build.</para></summary>
  public Ag Build(Ag? existing, ModelConfig config)
  {
    ArgumentNullException.ThrowIfNull(config);
    if (existing is not null && CurrentConfig is not null && CurrentConfig == config)
    {
      return existing;
    }

    CurrentConfig = config;
    CurrentSessionId = ResolveSessionId();
    // One accountant per window: a rebuild with a different model (hence window)
    // restarts accounting — mixing windows in one accumulator would lie about totals.
    Accountant = new ContextAccountant(config.ContextWindow);
    Current = new Ag(_provider, _conversation, config, _tools, BuildOptions());
    return Current;
  }

  /// <summary>The construction options for a build: the root's persisted identity, the
  ///     session's shaping collaborators, and the continuous transcript sink.</summary>
  private AgentOptions BuildOptions() => new()
  {
    // The root agent's in-memory identity IS the persisted root row id: children spawned
    // by the root record this id as their ParentId, so a fresh random id here would
    // phantom-parent every child (2026-10-04: all children pointed at a parent id that
    // had no row). Legacy wiring without a session id keeps the generated-id behavior.
    Id = ResolveRootId(),
    SystemPrompt = _dependencies?.SystemPrompt,
    MaxAutoContinuations = _dependencies?.MaxAutoContinuations ?? Ag.DefaultMaxAutoContinuations,
    ContextMonitor = Accountant,
    ContextCompactor = _dependencies?.ContextCompactor,
    TranscriptSink = _dependencies?.TranscriptSink,
    ServerToolImages = _dependencies?.ServerToolImages,
    EventLog = _dependencies?.EventLog,
    SessionId = ResolveSessionId(),
  };

  /// <summary>The persisted root id parsed out of the session identity, or null when the
  ///     wiring carries no id (legacy) or it is not a GUID.</summary>
  private AgentId? ResolveRootId() => ResolveSessionId() is { } idText && Guid.TryParse(idText, out Guid parsed)
      ? new AgentId(parsed)
      : null;


}
