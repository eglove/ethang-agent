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
///     fake (task 9). Everything between the runner and the provider is REAL: the real
///     <see cref="RootRunHost"/>, the real <see cref="SendMessageCommandHandler"/>, the real
///     agent loop, and a real <see cref="Conversation"/>.
///     <para>Why the container rather than a hand-built runner: the production host resolves
///     its turn handler from the container, so a double that bypassed the container would
///     pass while the host's own resolution was broken.</para></summary>
internal sealed class ScriptedRootHost : IDisposable
{
  private readonly ServiceProvider _services;

  private ScriptedRootHost(ServiceProvider services, RootRunHost runner)
  {
    _services = services;
    Runner = runner;
  }

  /// <summary>The runner under test, resolved exactly the way the server resolves it.</summary>
  public RootRunHost Runner { get; }

  /// <summary>Builds a host whose provider streams <paramref name="deltas"/> and then
  ///     completes with <paramref name="answer"/>. <paramref name="betweenDeltas"/> runs
  ///     between the first and second delta, so a test can observe what the caller had
  ///     received mid-turn. <paramref name="providerError"/> fails the provider call instead.</summary>
  public static ScriptedRootHost Start(string answer, IReadOnlyList<string> deltas,
      Action? betweenDeltas = null, DomainError? providerError = null)
  {
    Conversation conversation = new();
    IToolRegistry tools = new EmptyToolRegistry();
    IModelProvider provider = new ScriptedProvider(answer, deltas, betweenDeltas, providerError);
    ModelConfig config = ModelConfig.Create("scripted/model", null, 4096, 0f, 200_000).Value!;
    AgentId rootId = AgentId.NewId();
    Ag agent = new(provider, conversation, config, tools, new AgentOptions { Id = rootId });

    ServiceProvider services = new ServiceCollection()
        .AddSingleton(conversation)
        .AddSingleton(tools)
        .AddSingleton(provider)
        .AddSingleton(new SendMessageCommandHandler(agent: agent, conversation: conversation))
        .BuildServiceProvider();
    return new ScriptedRootHost(services, new RootRunHost(services, rootId, conversation));
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
  private sealed class ScriptedProvider(
      string answer,
      IReadOnlyList<string> deltas,
      Action? betweenDeltas,
      DomainError? providerError) : IModelProvider
  {
    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
        => SendStreamingAsync(config, request, null, null, ct);

    public Task<Result<ModelResponse>> SendStreamingAsync(ModelConfig config, ModelRequest request,
        Action<string>? onContentDelta = null, Action<string>? onReasoningDelta = null,
        CancellationToken ct = default)
    {
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
}
