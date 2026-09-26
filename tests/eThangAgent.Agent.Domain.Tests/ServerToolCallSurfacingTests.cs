using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Server-side tool calls (web_search etc.) execute inside the provider's
///     response and never enter the message history — the loop must surface them as
///     System lines so the user's transcript shows what the provider did.</summary>
public class ServerToolCallSurfacingTests
{
  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", null, 100, 0.5f, 8192).Value!;

  [Fact]
  public async Task ResponseWithServerToolCalls_FiresSystemLines()
  {
    ScriptedProvider provider = new(new ModelResponse("done", [],
        ServerToolCalls: [new ServerToolCall("web_search", "best coffee grinder")]));
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]));
    List<string> surfaced = [];

    Result<string> result = await agent.SendMessage("hi",
        new TurnCallbacks(OnSystemMessage: surfaced.Add), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    string line = Assert.Single(surfaced);
    Assert.Equal("[web_search] best coffee grinder", line);
    // The line also lands in the conversation so a resumed session sees it.
    Assert.Contains(agent.Conversation.Messages, m => m.Role is Role.System && m.Content == "[web_search] best coffee grinder");
  }

  [Fact]
  public async Task ServerToolCallWithoutDetail_ShowsToolOnly()
  {
    ScriptedProvider provider = new(new ModelResponse("done", [],
        ServerToolCalls: [new ServerToolCall("web_fetch", null)]));
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]));
    List<string> surfaced = [];

    _ = await agent.SendMessage("hi",
        new TurnCallbacks(OnSystemMessage: surfaced.Add), ct: TestContext.Current.CancellationToken);

    Assert.Equal("[web_fetch]", Assert.Single(surfaced));
  }

  [Fact]
  public async Task PlainResponse_NoSystemLines()
  {
    ScriptedProvider provider = new(new ModelResponse("done", []));
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]));
    List<string> surfaced = [];

    _ = await agent.SendMessage("hi",
        new TurnCallbacks(OnSystemMessage: surfaced.Add), ct: TestContext.Current.CancellationToken);

    Assert.Empty(surfaced);
  }

  private sealed class ScriptedProvider(ModelResponse response) : IModelProvider
  {
    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
        => Task.FromResult(Result.Success(response));
  }
}
