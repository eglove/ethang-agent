using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.AgentInfrastructure;
using eThangAgent.ChildHost;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;
using Ag = eThangAgent.AgentDomain.Agent;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>The optional per-host knobs a test may supply: a pre-filled root identity
///     carrier and a mid-turn park. Grouped so the host factory stays inside the
///     parameter budget as seams accumulate.</summary>
internal sealed record ScriptedOptions(
    RootSessionIdentity? Identity = null,
    ScriptedPark? Park = null,
    bool StoreIgnoresToken = false,
    TimeSpan? ParkBeforeFirstDelta = null,
    // Issue #245 test knobs: the reasoning fragments the provider streams, and the
    // tool-call iteration (calls + the one tool's fixed result content) a turn runs
    // before its answer. Grouped here so the factory stays inside the parameter budget.
    IReadOnlyList<string>? ReasoningDeltas = null,
    IReadOnlyList<ToolCallRequest>? ToolCalls = null,
    string? ToolResultContent = null,
    // Issue #255 test knob: the provider-scored usage the scripted model reports. Null
    // (the default) reports none, so the accountant stays empty and no context event
    // fires - the shape every pre-existing test relies on.
    TokenUsage? Usage = null,
    // Issue #253 test knob: the ROOT steering inbox the handler drains. Production
    // resolves the container's IAgentInbox here; a rig that omits it has no steering
    // surface at all (the pre-#253 shape).
    IAgentInbox? Inbox = null,
    // Issue #24 test knob: the append-only event log the loop records usage rows to, and
    // the prices it stamps. Null (the default) records nothing, so no `usage` event fires -
    // the shape every pre-existing test relies on.
    IAgentEventLog? EventLog = null,
    IUsagePriceSource? UsagePrices = null);

#pragma warning disable CA1812 // Instantiated by the gate tests, not by this file.
/// <summary>The optional mid-turn park a streaming test needs: an ASYNC hook that runs
///     between the provider's deltas, so a test can hold a turn open on a genuine yield
///     (not a blocking wait) while it drives other work. The hook receives the TURN's
///     cancellation token, so a parked turn can be stopped the way a real one is.</summary>
internal sealed record ScriptedPark(Func<CancellationToken, Task> BetweenDeltas);
#pragma warning restore CA1812

/// <summary>A composed host container for ONE root turn, with the model provider as the only
///     fake. Everything between the runner and the provider is REAL: the real
///     <see cref="RootRunHost"/>, the real <see cref="SendMessageCommandHandler"/>, the real
///     agent loop, and a real <see cref="Conversation"/>.
///     <para>Why the container rather than a hand-built runner: the production host resolves
///     its turn handler from the container, so a double that bypassed the container would
///     pass while the host's own resolution was broken.</para></summary>
internal sealed class ScriptedRootHost : IDisposable
{
  private readonly ServiceProvider _services;

  private ScriptedRootHost(ServiceProvider services, RootRunHost runner, RootSessionIdentity identity,
      ScriptedProvider provider, ChildHostServer server)
  {
    _services = services;
    Runner = runner;
    RootIdentity = identity;
    Provider = provider;
    Server = server;
  }

  /// <summary>The runner under test, resolved exactly the way the server resolves it.</summary>
  public RootRunHost Runner { get; }

  /// <summary>The container's root identity carrier — the object a host turn must fill or the
  ///     root transcript sink stays inert.</summary>
  public RootSessionIdentity RootIdentity { get; }

  /// <summary>The scripted model, exposed so a test can observe how many provider calls have
  ///     started (the signal that a turn is genuinely in flight).</summary>
  public ScriptedProvider Provider { get; }

  /// <summary>How many provider calls have started.</summary>
  public int ProviderCalls => Provider.Calls;

  /// <summary>The REAL server this host is served through, so a test can attach its own
  ///     delivery registries and drive other envelope kinds while a turn is in flight.</summary>
  public ChildHostServer Server { get; }

  /// <summary>How many events the host has forwarded toward the app. A rig has no app
  ///     attached, so every wire write is dropped by contract - the COUNT is what proves
  ///     the ROOT container's forwarder is subscribed at all.</summary>
  public long ForwardedEventCount => Server.ForwardedEventCount;

  /// <summary>Builds a host whose provider streams <paramref name="deltas"/> and then
  ///     completes with <paramref name="answer"/>. <paramref name="betweenDeltas"/> runs
  ///     between the first and second delta, so a test can observe what the caller had
  ///     received mid-turn. <paramref name="providerError"/> fails the provider call instead.
  ///     <paramref name="options"/> supplies the grouped knobs (a pre-filled root identity
  ///     carrier and a mid-turn park).</summary>
  public static ScriptedRootHost Start(string answer, IReadOnlyList<string> deltas,
      Action? betweenDeltas = null, DomainError? providerError = null,
      IReadOnlyList<Message>? storedHistory = null, TimeSpan? storeDelay = null,
      ScriptedOptions? options = null)
  {
    RootSessionIdentity? identity = options?.Identity;
    ScriptedPark? park = options?.Park;
    Conversation conversation = new();
    IToolRegistry tools = options?.ToolCalls is { Count: > 0 }
        ? new ScriptedToolRegistry(options.ToolResultContent ?? "")
        : new EmptyToolRegistry();
    ScriptedProvider provider = new(answer, deltas, betweenDeltas, providerError, park?.BetweenDeltas,
        options ?? new ScriptedOptions());
    ModelConfig config = ModelConfig.Create("scripted/model", 4096, 0f, 200_000).Value!;
    AgentId rootId = AgentId.NewId();
    // The context accountant is wired exactly as production wires it (RootAgentHolder
    // builds one per resolved window): the loop's OnContextUpdate only fires when a
    // monitor exists, so a harness without one could never observe the `context` wire
    // kind (issue #255).
    Ag agent = new(provider, conversation, config, tools,
        new AgentOptions
        {
          Id = rootId,
          ContextMonitor = new ContextAccountant(config.ContextWindow),
          EventLog = options?.EventLog,
          UsagePrices = options?.UsagePrices,
          WorkspaceId = options?.EventLog is null ? null : "ws-scripted",
        });
    RootSessionIdentity carrier = identity ?? new RootSessionIdentity();

    RecordingStore store = new(storedHistory ?? [], storeDelay ?? TimeSpan.Zero,
        options?.StoreIgnoresToken ?? false);
    IServiceCollection registrations = new ServiceCollection()
        .AddSingleton(conversation)
        .AddSingleton(tools)
        .AddSingleton<IModelProvider>(provider)
        .AddSingleton(carrier)
        .AddSingleton<IAgentStore>(store)
        // Production containers always register the session event stream, and the host's
        // root-event forwarder resolves it to republish facts toward the app. A rig
        // without it would silently skip forwarding and could not observe the wiring
        // (issue #8 follow-up).
        .AddSingleton<IAgentEvents, InProcessAgentEvents>()
        .AddSingleton(new SendMessageCommandHandler(agent: agent, conversation: conversation,
            inbox: options?.Inbox));
    // Production registers the ROOT steering inbox as the container's IAgentInbox and hands
    // that same instance to the handler (AgentComposition). The host's root-steer delivery
    // resolves it from the container, so a rig that only passed it to the handler would
    // test a wiring production never has (issue #253).
    if (options?.Inbox is { } rootInbox)
    {
      _ = registrations.AddSingleton(rootInbox);
    }

    ServiceProvider services = registrations.BuildServiceProvider();
    RootRunHost runner = new(services, rootId, conversation);
    // The REAL server, over a throwaway settings file and database: the tests that drive
    // envelopes need the production dispatch path, and the runner is attached through the
    // server's own seam so no provider stack is built.
    string settings = Path.Combine(Path.GetTempPath(), "ethang-scripted-" + Guid.NewGuid().ToString("N") + ".json");
    File.WriteAllText(settings, /*lang=json,strict*/ "{\"OpenRouter\":{\"ApiKey\":null},\"SubAgents\":{}}");
    ChildHostServer server = new(settings,
        Path.Combine(Path.GetTempPath(), "ethang-scripted-" + Guid.NewGuid().ToString("N") + ".db"));
    server.AttachRootRun(runner);
    return new ScriptedRootHost(services, runner, carrier, provider, server);
  }

  /// <summary>Runs one turn through the runner exactly as the server does.</summary>
  public Task<Result<string>> RunTurnAsync(RootTurnRequest request, Action<RootTurnEvent> onEvent,
      CancellationToken ct)
      => Runner.RunTurnAsync(request, onEvent, ct);

  public void Dispose() => _services.Dispose();

  /// <summary>A registry with no tools: these tests exercise the turn path, not tool dispatch.</summary>
  private sealed class EmptyToolRegistry : IToolRegistry
  {
    public ITool? Find(string name) => null;

    public IReadOnlyList<ToolDefinition> Definitions => [];
  }

  /// <summary>A registry whose one tool returns a FIXED content string: the tool-result
  /// forwarding tests need a tool whose output is distinguishable from the "ok"
  /// summary the legacy wire shape carried (issue #245).</summary>
  private sealed class ScriptedToolRegistry(string content) : IToolRegistry
  {
    public ITool? Find(string name) => name == "probe"
        ? new ProbeTool(content)
        : null;

    public IReadOnlyList<ToolDefinition> Definitions => [new ToolDefinition("probe", "probe", [])];

    private sealed class ProbeTool(string content) : ITool
    {
      public ToolDefinition Definition { get; } = new("probe", "probe", []);

      public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
          => Task.FromResult(new ToolResult(content, false));
    }
  }

  /// <summary>The scripted model: emits the deltas in order, then completes with the answer.
  ///     Both provider entry points stream, so the agent's choice of call cannot hide a
  ///     delta from the runner.</summary>
  internal sealed class ScriptedProvider(
      string answer,
      IReadOnlyList<string> deltas,
      Action? betweenDeltas,
      DomainError? providerError,
      Func<CancellationToken, Task>? parkBetweenDeltas,
      ScriptedOptions options) : IModelProvider
  {
    private int _calls;
    private int _toolIterationDone;
    private IReadOnlyList<Message> _lastRequest = [];
    private readonly List<IReadOnlyList<Message>> _requests = [];

    /// <summary>How many provider calls have started.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Every message list the model was handed, in call order — the observation a
    ///     concurrency test needs to prove EVERY turn saw the hydrated prefix.</summary>
    public IReadOnlyList<IReadOnlyList<Message>> Requests
    {
      get
      {
        lock (_requests)
        {
          return [.. _requests];
        }
      }
    }

    /// <summary>The message list the model was handed on the most recent call — the only
    ///     direct observation of what history a turn actually saw.</summary>
    public IReadOnlyList<Message> LastRequest => Volatile.Read(ref _lastRequest);

    /// <summary>The contents of the last request, in order.</summary>
    public IReadOnlyList<string> LastRequestTexts
        => [.. LastRequest.Select(message => message.Content)];

    /// <summary>The roles of the last request, in order.</summary>
    public IReadOnlyList<string> LastRequestRoles
        => [.. LastRequest.Select(message => message.Role.ToString())];

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
        => SendStreamingAsync(config, request, null, null, ct);

    public async Task<Result<ModelResponse>> SendStreamingAsync(ModelConfig config, ModelRequest request,
        Action<string>? onContentDelta = null, Action<string>? onReasoningDelta = null,
        CancellationToken ct = default)
    {
      _ = Interlocked.Increment(ref _calls);
      Volatile.Write(ref _lastRequest, request.Messages);
      if (options.ParkBeforeFirstDelta is { } delay)
      {
        await Task.Delay(delay, ct).ConfigureAwait(false);
      }
      lock (_requests)
      {
        _requests.Add(request.Messages);
      }
      if (providerError is not null)
      {
        return Result.Failure<ModelResponse>(providerError);
      }

      for (int index = 0; index < deltas.Count; index++)
      {
        onContentDelta?.Invoke(deltas[index]);
        if (index == 0)
        {
          betweenDeltas?.Invoke();
          if (parkBetweenDeltas is not null)
          {
            await parkBetweenDeltas(ct).ConfigureAwait(false);
          }
        }
      }

      foreach (string reasoning in options.ReasoningDeltas ?? [])
      {
        onReasoningDelta?.Invoke(reasoning);
      }

      if (options.ToolCalls is { Count: > 0 } calls && Interlocked.Exchange(ref _toolIterationDone, 1) == 0)
      {
        // One tool-call iteration: the loop executes the calls (each resolved through the
        // registry) and re-enters the provider, whose SECOND call returns the answer.

        return Result.Success(new ModelResponse("", calls));
      }

      // The provider-scored usage (issue #255): the agent's accountant is the only
      // context source, so a scripted model that reports none produces no snapshot.
      return Result.Success(new ModelResponse(answer, [], FinishReason.Stop, options.Usage));
    }
  }

  /// <summary>A store that serves a fixed transcript and counts appends. Only the two
  ///     members the root turn path touches are implemented; everything else throws, so a
  ///     turn that strays into an unexpected store call fails loudly instead of silently
  ///     succeeding.
  ///     <para>Why a hand-written double rather than the real SQLite store: these tests
  ///     assert what the MODEL was handed and what was persisted, and a fake makes both
  ///     directly observable.</para></summary>
  internal sealed class RecordingStore(IReadOnlyList<Message> transcript, TimeSpan delay,
      bool ignoresToken) : IAgentStore
  {
    /// <summary>A deliberately SLOW read: concurrency defects only appear when the store
    ///     read is wide enough for a second turn to arrive inside it.</summary>
    public async Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id,
        CancellationToken ct = default)
    {
      // The second store shape the review measured: a store that does NOT observe the token.
      // The real SqliteAgentStore passes ct to ExecuteReaderAsync, but a store that ignores it
      // must still not let an abandoned hydration mutate the conversation.
      await Task.Delay(delay, ignoresToken ? CancellationToken.None : ct).ConfigureAwait(false);
      return Result.Success(transcript);
    }

    public Task<Result<string>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default)
        => Task.FromResult(Result.Success("ok"));

    public Task<Result<string>> ReplaceTranscriptAsync(AgentId id, IReadOnlyList<Message> messages,
        CancellationToken ct = default)
        => Task.FromResult(Result.Success("ok"));

    public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
        => throw new NotSupportedException("SaveAsync is outside the root turn path.");

    public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
        => throw new NotSupportedException("UpdateAsync is outside the root turn path.");

    public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
        => throw new NotSupportedException("GetAsync is outside the root turn path.");

    public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId,
        CancellationToken ct = default)
        => throw new NotSupportedException("ListChildrenAsync is outside the root turn path.");

    public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
        => throw new NotSupportedException("ListAllAsync is outside the root turn path.");
  }
}
