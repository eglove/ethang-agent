using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.ChildHost;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>The app's LIVE model choice must reach the model that serves the turn
///     (2026-10-22). The picker lives in the app and the conversation's agent lives in the
///     host process, so the choice travels on the root turn request; a turn that dropped it
///     resolved from the persisted row - which the bootstrap stamps with the routing id,
///     i.e. "nothing selected" - and served <c>openrouter/auto</c> while the app displayed
///     the user's pick.
/// <para>Anti-tautology: the container below is the REAL host turn path - the real
///     <see cref="RootRunHost"/>, the real <see cref="SendMessageCommandHandler"/>, the real
///     <see cref="RootAgentResolver"/>, and the real agent loop. Only the model provider and
///     the store are doubles, and the provider records the config it was handed, which is
///     the only direct observation of what actually served.</para></summary>
public sealed class ChildHostRootModelPreferenceTests
{
  private const string FallbackModel = "openrouter/auto";
  private const string PickedModel = "minimax/minimax-m3.1-flash:free";

  private static async Task<(RootRunHost Runner, RecordingProvider Provider, SessionModelPreferences Live)>
      StartHostAsync(string? pinnedModel)
  {
    AgentId rootId = AgentId.NewId();
    Conversation conversation = new();
    RecordingProvider provider = new();
    SessionModelPreferences preferences = new();
    RootSessionIdentity identity = new() { Id = rootId };
    StubStore store = new(rootId, pinnedModel);

    RootAgentHolder holder = new(provider, conversation, new EmptyToolRegistry());
    RootAgentResolver resolver = new(
        new RootModelContext(store, identity, FallbackModel, 4096, 0f, new FixedWindow()),
        preferences);
    SendMessageCommandHandler handler = new(
        conversation: conversation,
        rootTurn: new RootTurnServices(holder, resolver));

    ServiceProvider services = new ServiceCollection()
        .AddSingleton(conversation)
        .AddSingleton<IToolRegistry>(new EmptyToolRegistry())
        .AddSingleton<IModelProvider>(provider)
        .AddSingleton(identity)
        .AddSingleton<IAgentStore>(store)
        .AddSingleton(preferences)
        .AddSingleton(handler)
        .BuildServiceProvider();

    RootRunHost runner = new(services, rootId, conversation);
    await Task.CompletedTask.ConfigureAwait(true);
    return (runner, provider, preferences);
  }

  [Fact]
  public async Task RunTurnAsync_CarriedModelChoice_ServesThatModel_NotTheFallback()
  {
    (RootRunHost runner, RecordingProvider provider, _) = await StartHostAsync(pinnedModel: FallbackModel).ConfigureAwait(true);
    RootTurnRequest request = new("turn-1", "hello", [])
    {
      ModelPreferences = SessionModelPreferencesJson.Serialize(new SessionModelPreferences { ModelId = PickedModel }),
    };

    Result<string> result = await runner.RunTurnAsync(request, _ => { },
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    Assert.Equal(PickedModel, provider.LastModelId);
  }

  [Fact]
  public async Task RunTurnAsync_CarriedModelChoice_LandsOnTheContainersLivePreferences()
  {
    // Consumers hold the container's OWN preferences instance (the child spawner reads it
    // on every spawn), so the travelled values must be applied to it, not to a replacement.
    (RootRunHost runner, _, SessionModelPreferences live) = await StartHostAsync(pinnedModel: FallbackModel).ConfigureAwait(true);
    RootTurnRequest request = new("turn-1", "hello", [])
    {
      ModelPreferences = SessionModelPreferencesJson.Serialize(new SessionModelPreferences
      {
        ModelId = PickedModel,
        ReasoningEffort = ReasoningEffort.Max,
      }),
    };

    _ = await runner.RunTurnAsync(request, _ => { }, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal(PickedModel, live.ModelId);
    Assert.Equal(ReasoningEffort.Max, live.ReasoningEffort);
  }

  [Fact]
  public async Task RunTurnAsync_MalformedCarrier_SettlesWithATypedRefusal_NeverServesAFallback()
  {
    (RootRunHost runner, RecordingProvider provider, _) = await StartHostAsync(pinnedModel: FallbackModel).ConfigureAwait(true);
    List<RootTurnEvent> events = [];
    RootTurnRequest request = new("turn-1", "hello", []) { ModelPreferences = "{not json" };

    Result<string> result = await runner.RunTurnAsync(request, events.Add,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidModelPreferences", result.Error.Code);
    Assert.Equal(0, provider.Calls);
    RootTurnEvent settle = Assert.Single(events);
    Assert.Equal("settled", settle.Kind);
    Assert.Contains("InvalidModelPreferences", settle.Payload, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RunTurnAsync_NoCarrier_LeavesTheHostsOwnResolutionAlone()
  {
    // A headless or test host that resolves on its own keeps doing so: the pin serves.
    (RootRunHost runner, RecordingProvider provider, _) = await StartHostAsync(pinnedModel: "google/gemini-3.8-flash").ConfigureAwait(true);

    Result<string> result = await runner.RunTurnAsync(new RootTurnRequest("turn-1", "hello", []), _ => { },
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    Assert.Equal("google/gemini-3.8-flash", provider.LastModelId);
  }

  [Fact]
  public async Task RunTurnAsync_NoCarrierAndNothingSelected_FailsNoModelSelected()
  {
    // No carrier and no pin: the host refuses rather than substituting the routing model.
    (RootRunHost runner, RecordingProvider provider, _) = await StartHostAsync(pinnedModel: FallbackModel).ConfigureAwait(true);
    List<RootTurnEvent> events = [];

    Result<string> result = await runner.RunTurnAsync(new RootTurnRequest("turn-1", "hello", []), events.Add,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("NoModelSelected", result.Error.Code);
    Assert.Equal(0, provider.Calls);
    Assert.Contains("NoModelSelected", Assert.Single(events).Payload, StringComparison.Ordinal);
  }

  private sealed class FixedWindow : IContextWindowSource
  {
    public Task<int?> WindowForAsync(string modelId, CancellationToken ct = default)
        => Task.FromResult<int?>(200_000);
  }

  /// <summary>Records the model id of every config the loop handed it - the direct
  ///     observation of what actually served the turn.</summary>
  private sealed class RecordingProvider : IModelProvider
  {
    public string? LastModelId { get; private set; }

    public int Calls { get; private set; }

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
    {
      LastModelId = config.ModelId;
      Calls++;
      return Task.FromResult(Result.Success(new ModelResponse("ok", [])));
    }
  }

  private sealed class EmptyToolRegistry : IToolRegistry
  {
    public ITool? Find(string name) => null;

    public IReadOnlyList<ToolDefinition> Definitions => [];
  }

  /// <summary>Serves one root record carrying <paramref name="pinnedModel"/>; the two
  ///     members the turn path does not touch throw, so a stray call fails loudly.</summary>
  private sealed class StubStore(AgentId rootId, string? pinnedModel) : IAgentStore
  {
    public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Success(AgentRecord.Root(rootId, DateTimeOffset.UtcNow,
            @"C:\workspaces\demo", "openrouter", pinnedModel)));

    public Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<Message>>([]));

    public Task<Result<string>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default)
        => Task.FromResult(Result.Success("ok"));

    public Task<Result<string>> ReplaceTranscriptAsync(AgentId id, IReadOnlyList<Message> messages, CancellationToken ct = default)
        => Task.FromResult(Result.Success("ok"));

    public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success(record.Id.ToString()));

    public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success(record.Id.ToString()));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([]));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([]));
  }
}
