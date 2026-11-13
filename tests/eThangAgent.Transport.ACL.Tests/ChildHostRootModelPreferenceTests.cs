using System.Text.Json;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.ChildHost;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.OpenRouter.ACL;
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
[Trait("Category", "Process")]
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
      ModelPreferences = SessionModelPreferencesJson.Serialize(new SessionModelPreferences { ModelIds = [PickedModel] }),
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
        ModelIds = [PickedModel],
        ReasoningEffort = ReasoningEffort.Max,
      }),
    };

    _ = await runner.RunTurnAsync(request, _ => { }, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal([PickedModel], live.ModelIds);
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

  private const string Head = "alpha/model";
  private const string Second = "beta/other";
  private const string Third = "gamma/third";

  [Fact]
  public async Task AnOrderedList_ReachesAHostRunTurn_AsTheSameModelsArrayAnInProcessTurnSends()
  {
    // THE WHOLE POINT OF THE CARRIER: the app owns the picker and the agent lives in its own host,
    // so an ordered list that did not travel would leave the host resolving from the persisted pin
    // - the wrong model FIRST, and no fallbacks at all. The two turns below are the same session
    // seen from either side of that boundary, and the comparison is made on the request body the
    // provider actually receives, not on the resolver's intermediate value.
    (RootRunHost runner, RecordingProvider provider, _) = await StartHostAsync(pinnedModel: FallbackModel)
        .ConfigureAwait(true);
    SessionModelPreferences preferences = new()
    {
      ModelIds = [Head, Second, Third],
      ModelVariant = ModelVariant.Nitro,
    };

    RootTurnRequest request = new("turn-1", "hello", [])
    {
      ModelPreferences = SessionModelPreferencesJson.Serialize(preferences),
    };
    Result<string> hostTurn = await runner.RunTurnAsync(request, _ => { },
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(hostTurn.IsSuccess, hostTurn.Error?.Message);
    Assert.NotNull(provider.LastConfig);

    // The in-process turn reads the app's own live preferences instance directly - the same value
    // the carrier above encodes - so the two paths differ only in how the list arrived.
    RootAgentResolver inProcess = new(
        new RootModelContext(null, null, FallbackModel, 4096, 0f, new FixedWindow()), preferences);
    Result<(ModelConfig Config, string? Notice)> inProcessTurn =
        await inProcess.ResolveAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(inProcessTurn.IsSuccess, inProcessTurn.Error?.Message);

    IReadOnlyList<string> hostModels =
        await ModelsAsync(provider.LastConfig).ConfigureAwait(true);
    IReadOnlyList<string> inProcessModels =
        await ModelsAsync(inProcessTurn.Value.Config).ConfigureAwait(true);

    Assert.Equal(inProcessModels, hostModels);
    Assert.Equal([Head + ":nitro", Second + ":nitro", Third + ":nitro"], hostModels);
  }

  [Fact]
  public async Task AnOrderedListWithABackup_ThatTheUserDidNotPick_IsNeverServedInPlaceOfTheHead()
  {
    // Order is the whole statement: the head runs, so the config the provider is handed names it
    // first and lists the backups after it.
    (RootRunHost runner, RecordingProvider provider, _) = await StartHostAsync(pinnedModel: FallbackModel)
        .ConfigureAwait(true);
    RootTurnRequest request = new("turn-1", "hello", [])
    {
      ModelPreferences = SessionModelPreferencesJson.Serialize(new SessionModelPreferences
      {
        ModelIds = [Head, Second],
      }),
    };

    Result<string> result = await runner.RunTurnAsync(request, _ => { },
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    Assert.Equal(Head, provider.LastModelId);
    Assert.Equal([Second], provider.LastConfig!.FallbackModelIds);
  }

  /// <summary>Renders a config through the REAL OpenRouter body builder and returns the ordered
  ///     <c>models</c> array it puts on the wire - empty when the request carries no such member,
  ///     which is what a single-model session sends.</summary>
#pragma warning disable CA2000 // HttpClient owns the handler; the using below bounds it
  private static async Task<IReadOnlyList<string>> ModelsAsync(ModelConfig config)
  {
    string? captured = null;
    CapturingHandler handler = new(async request =>
    {
      captured = request.Content is { } content
          ? await content.ReadAsStringAsync().ConfigureAwait(false)
          : null;
      return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
      {
        Content = new StringContent(
            /*lang=json,strict*/ "\u007B\"status\":\"completed\",\"output\":[]\u007D"),
      };
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider modelProvider = new(
        http, new OpenRouterConfiguration("test-key", new Uri("https://openrouter.test")));

    Result<ModelResponse> sent = await modelProvider.SendAsync(config,
        new ModelRequest([new Message(Role.User, "hi", DateTimeOffset.UtcNow)]),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(sent.IsSuccess, sent.Error?.Message);

    using JsonDocument body = JsonDocument.Parse(captured!);
    return body.RootElement.TryGetProperty("models", out JsonElement models)
        ? [.. models.EnumerateArray().Select(entry => entry.GetString()!)]
        : [];
  }
#pragma warning restore CA2000

  /// <summary>Captures the request body the provider sends and answers with a minimal completed
  ///     response.</summary>
  private sealed class CapturingHandler(
      Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken) => respond(request);
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
    public string? LastModelId => LastConfig?.ModelId;

    /// <summary>The whole config the loop handed the provider for the last turn: the direct
    ///     observation of the head, the fallbacks, and every overlaid knob.</summary>
    public ModelConfig? LastConfig { get; private set; }

    public int Calls { get; private set; }

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
    {
      LastConfig = config;
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

    public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([]));
  }
}
