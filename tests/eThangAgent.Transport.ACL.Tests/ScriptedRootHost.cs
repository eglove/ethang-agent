using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.ChildHost;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;
using Ag = eThangAgent.AgentDomain.Agent;

namespace eThangAgent.Transport.ACL.Tests;

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

  /// <summary>Builds a host whose provider streams <paramref name="deltas"/> and then
  ///     completes with <paramref name="answer"/>. <paramref name="betweenDeltas"/> runs
  ///     between the first and second delta, so a test can observe what the caller had
  ///     received mid-turn. <paramref name="providerError"/> fails the provider call instead.
  ///     <paramref name="identity"/> supplies a pre-filled root identity carrier.</summary>
  public static ScriptedRootHost Start(string answer, IReadOnlyList<string> deltas,
      Action? betweenDeltas = null, DomainError? providerError = null,
      RootSessionIdentity? identity = null, IReadOnlyList<Message>? storedHistory = null,
      TimeSpan? storeDelay = null)
  {
    Conversation conversation = new();
    IToolRegistry tools = new EmptyToolRegistry();
    ScriptedProvider provider = new(answer, deltas, betweenDeltas, providerError);
    ModelConfig config = ModelConfig.Create("scripted/model", null, 4096, 0f, 200_000).Value!;
    AgentId rootId = AgentId.NewId();
    Ag agent = new(provider, conversation, config, tools, new AgentOptions { Id = rootId });
    RootSessionIdentity carrier = identity ?? new RootSessionIdentity();

    RecordingStore store = new(storedHistory ?? [], storeDelay ?? TimeSpan.Zero);
    ServiceProvider services = new ServiceCollection()
        .AddSingleton(conversation)
        .AddSingleton(tools)
        .AddSingleton<IModelProvider>(provider)
        .AddSingleton(carrier)
        .AddSingleton<IAgentStore>(store)
        .AddSingleton(new SendMessageCommandHandler(agent: agent, conversation: conversation))
        .BuildServiceProvider();
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

  /// <summary>The scripted model: emits the deltas in order, then completes with the answer.
  ///     Both provider entry points stream, so the agent's choice of call cannot hide a
  ///     delta from the runner.</summary>
  internal sealed class ScriptedProvider(
      string answer,
      IReadOnlyList<string> deltas,
      Action? betweenDeltas,
      DomainError? providerError) : IModelProvider
  {
    private int _calls;
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

    public Task<Result<ModelResponse>> SendStreamingAsync(ModelConfig config, ModelRequest request,
        Action<string>? onContentDelta = null, Action<string>? onReasoningDelta = null,
        CancellationToken ct = default)
    {
      _ = Interlocked.Increment(ref _calls);
      Volatile.Write(ref _lastRequest, request.Messages);
      lock (_requests)
      {
        _requests.Add(request.Messages);
      }
      if (providerError is not null)
      {
        return Task.FromResult(Result.Failure<ModelResponse>(providerError));
      }

      for (int index = 0; index < deltas.Count; index++)
      {
        onContentDelta?.Invoke(deltas[index]);
        if (index == 0)
        {
          betweenDeltas?.Invoke();
        }
      }

      return Task.FromResult(Result.Success(new ModelResponse(answer, [])));
    }
  }

  /// <summary>A store that serves a fixed transcript and counts appends. Only the two
  ///     members the root turn path touches are implemented; everything else throws, so a
  ///     turn that strays into an unexpected store call fails loudly instead of silently
  ///     succeeding.
  ///     <para>Why a hand-written double rather than the real SQLite store: these tests
  ///     assert what the MODEL was handed and what was persisted, and a fake makes both
  ///     directly observable.</para></summary>
  internal sealed class RecordingStore(IReadOnlyList<Message> transcript, TimeSpan delay) : IAgentStore
  {
    private int _appended;

    /// <summary>A deliberately SLOW read: concurrency defects only appear when the store
    ///     read is wide enough for a second turn to arrive inside it.</summary>
    public async Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id,
        CancellationToken ct = default)
    {
      await Task.Delay(delay, ct).ConfigureAwait(false);
      return Result.Success(transcript);
    }

    public Task<Result<string>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default)
    {
      _ = Interlocked.Increment(ref _appended);
      return Task.FromResult(Result.Success("ok"));
    }

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
