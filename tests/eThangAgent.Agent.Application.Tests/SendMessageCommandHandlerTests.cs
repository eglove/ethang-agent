using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Ag = eThangAgent.AgentDomain.Agent;

namespace eThangAgent.Agent.Application.Tests;

public class SendMessageCommandHandlerTests
{
  private const string FallbackModel = "openrouter/auto";

  [Fact]
  public async Task Handle_DelegatesToAgentAndReturnsResult()
  {
    StubModelProvider provider = new(
        Result.Success(new ModelResponse("response", [])));
    Ag agent = new(provider, new Conversation(),
        ModelConfig.Create("m", null, 100, 0.5f, 8192).Value!, new ToolRegistry([]));
    SendMessageCommandHandler handler = new(agent);

    Result<string> result = await handler.Handle(new SendMessageCommand("hello"), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("response", result.Value);
  }

  [Fact]
  public async Task Handle_PropagatesFailure()
  {
    DomainError error = new("FAIL", "bad");
    StubModelProvider provider = new(Result.Failure<ModelResponse>(error));
    Ag agent = new(provider, new Conversation(),
        ModelConfig.Create("m", null, 100, 0.5f, 8192).Value!, new ToolRegistry([]));
    SendMessageCommandHandler handler = new(agent);

    Result<string> result = await handler.Handle(new SendMessageCommand("hello"), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal(error, result.Error);
  }

  [Fact]
  public async Task Handle_ReselectionWired_SurfacesNoticeAndServesReselectedPair()
  {
    // The coordinator is pre-driven to a pending crossing: at the turn-start safe
    // point it rewrites the pin to the measured-weight winner ("m/x" via B) BEFORE
    // resolution, so the resolver serves that pair this same turn and the notice
    // surfaces through onNotice.
    FakeAgentStore store = new();
    AgentId rootId = AgentId.NewId();
    _ = await store.SaveAsync(
        AgentRecord.Root(rootId, DateTimeOffset.UtcNow, "C:/workspaces/demo", "openrouter", "old/model"),
        TestContext.Current.CancellationToken);
    RootSessionIdentity identity = new() { Id = rootId };
    CountingCatalog catalog = new(P("A", 0.04m, 0.50m), P("B", 0.07m, 0.05m));
    DynamicReselectionCoordinator coordinator = new(
        catalog, () => SmartSelectionOptions.Default, preferences: null,
        CrossedPolicy(), store, identity, FallbackModel);
    Conversation conversation = new();
    CapturingProvider provider = new();
    RootAgentHolder holder = new(provider, conversation, new ToolRegistry([]));
    RootModelContext context = new(
        store, identity, FallbackModel, 2048, 0.7f, new FixedWindowSource(), catalog,
        () => SmartSelectionOptions.Default);
    RootAgentResolver resolver = new(context, new StubSelector());
    SendMessageCommandHandler handler = new(
        conversation: conversation, rootHolder: holder, rootResolver: resolver, reselection: coordinator);
    List<string> notices = [];

    Result<string> result = await handler.Handle(
        new SendMessageCommand("hello"), onNotice: notices.Add, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    string notice = Assert.Single(notices);
    Assert.Contains("Model selected: m/x via B", notice, StringComparison.Ordinal);
    Assert.Equal("m/x", provider.LastModelId);
  }

  private static MeasuredMixPolicy CrossedPolicy()
  {
    MeasuredMixPolicy policy = new(0.05m);
    for (int i = 0; i < 5; i++)
    {
      policy.Observe(new UsageObservation(100, 400));
    }

    return policy;
  }

  private static ModelProviderEntry P(string prov, decimal inM, decimal outM) =>
      new("m/x", prov, inM / 1_000_000m, outM / 1_000_000m, 128000, 0, true, false, 50, 80, null, null, null, null);

  private sealed class CountingCatalog(params ModelProviderEntry[] entries) : IModelCatalog
  {
    public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<ModelProviderEntry>>(entries));
  }

  private sealed class StubSelector : IModelSelector
  {
    public Task<Result<ModelSelectionResult>> SelectAsync(SmartSelectionOptions options, bool requireVision, CancellationToken ct = default)
        => Task.FromResult(Result.Success(new ModelSelectionResult("should-not-be-used", "P", "r")));
  }

  private sealed class CapturingProvider : IModelProvider
  {
    public string? LastModelId { get; private set; }

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
    {
      LastModelId = config.ModelId;
      return Task.FromResult(Result.Success(new ModelResponse("response", [])));
    }
  }

  private sealed class StubModelProvider(Result<ModelResponse> result) : IModelProvider
  {
    private readonly Result<ModelResponse> _result = result;

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
        => Task.FromResult(_result);
  }
}
