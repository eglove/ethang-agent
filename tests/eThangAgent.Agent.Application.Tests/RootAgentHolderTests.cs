using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Ag = eThangAgent.AgentDomain.Agent;

namespace eThangAgent.Agent.Application.Tests;

public class RootAgentHolderTests
{
  private sealed class StubModelProvider : IModelProvider
  {
    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
        => Task.FromResult(Result.Success(new ModelResponse("ok", [])));
    public Task<Result<ModelResponse>> SendStreamingAsync(ModelConfig config, ModelRequest request,
        Action<string>? onContentDelta = null, Action<string>? onReasoningDelta = null, CancellationToken ct = default)
        => SendAsync(config, request, ct);
  }

  private static readonly IToolRegistry Tools = new ToolRegistry([]);

  [Fact]
  public void Build_FirstCall_ConstructsAgent_WithGivenConfig()
  {
    StubModelProvider provider = new();
    Conversation conversation = new();
    RootAgentHolder holder = new(provider, conversation, Tools);
    ModelConfig config = ModelConfig.Create("first/model", null, 512, 0.5f, 8192).Value!;

    Ag agent = holder.Build(existing: null, config);

    Assert.NotNull(holder.Current);
    Assert.Same(agent, holder.Current);
    Assert.Equal("first/model", holder.CurrentConfig!.ModelId);
    Assert.Same(conversation, agent.Conversation);
  }

  [Fact]
  public void Build_DifferentConfig_Rebuilds_KeepingSharedConversation()
  {
    StubModelProvider provider = new();
    Conversation conversation = new();
    RootAgentHolder holder = new(provider, conversation, Tools);
    ModelConfig first = ModelConfig.Create("first/model", null, 512, 0.5f, 8192).Value!;
    ModelConfig second = ModelConfig.Create("second/model", null, 512, 0.5f, 8192).Value!;

    Ag firstAgent = holder.Build(null, first);
    Ag secondAgent = holder.Build(firstAgent, second);

    Assert.NotSame(firstAgent, secondAgent);
    Assert.Same(secondAgent, holder.Current);
    Assert.Equal("second/model", holder.CurrentConfig!.ModelId);
    // Shared conversation preserved across the rebuild.
    Assert.Same(conversation, secondAgent.Conversation);
  }

  [Fact]
  public void Build_SameConfig_ReturnsExistingAgent_NoRebuild()
  {
    StubModelProvider provider = new();
    Conversation conversation = new();
    RootAgentHolder holder = new(provider, conversation, Tools);
    ModelConfig config = ModelConfig.Create("same/model", null, 512, 0.5f, 8192).Value!;

    Ag first = holder.Build(null, config);
    Ag second = holder.Build(first, config);

    Assert.Same(first, second);
    Assert.Equal("same/model", holder.CurrentConfig!.ModelId);
  }

  [Fact]
  public async Task Build_NullConfig_Throws()
  {
    RootAgentHolder holder = new(new StubModelProvider(), new Conversation(), Tools);
    _ = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        Task.FromResult(holder.Build(null, null!)));
  }
}
