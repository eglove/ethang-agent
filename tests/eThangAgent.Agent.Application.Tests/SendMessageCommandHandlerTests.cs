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
        ModelConfig.Create("m", 100, 0.5f, 8192).Value!, new ToolRegistry([]));
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
        ModelConfig.Create("m", 100, 0.5f, 8192).Value!, new ToolRegistry([]));
    SendMessageCommandHandler handler = new(agent);

    Result<string> result = await handler.Handle(new SendMessageCommand("hello"), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal(error, result.Error);
  }

  [Fact]
  public async Task Handle_RootResolutionWired_SurfacesNotice()
  {
    // A pinned root serves its pin and announces the re-pin when the preference
    // changes; the notice surfaces through onNotice.
    FakeAgentStore store = new();
    AgentId rootId = AgentId.NewId();
    _ = await store.SaveAsync(
        AgentRecord.Root(rootId, DateTimeOffset.UtcNow, "C:/workspaces/demo", "openrouter", "old/model"),
        TestContext.Current.CancellationToken);
    RootSessionIdentity identity = new() { Id = rootId };
    Conversation conversation = new();
    CapturingProvider provider = new();
    RootAgentHolder holder = new(provider, conversation, new ToolRegistry([]));
    RootModelContext context = new(
        store, identity, FallbackModel, 2048, 0.7f, new FixedWindowSource());
    SessionModelPreferences preferences = new() { ModelId = "m/x" };
    RootAgentResolver resolver = new(context, preferences);
    SendMessageCommandHandler handler = new(
        conversation: conversation,
        rootTurn: new RootTurnServices(holder, resolver));
    List<string> notices = [];

    Result<string> result = await handler.Handle(
        new SendMessageCommand("hello"), onNotice: notices.Add, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    string notice = Assert.Single(notices);
    Assert.Contains("Model selected: m/x", notice, StringComparison.Ordinal);
    Assert.Equal("m/x", provider.LastModelId);
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
