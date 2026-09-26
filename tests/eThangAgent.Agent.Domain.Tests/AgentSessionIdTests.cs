using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
// FakeProvider (shared fake, records RequestsSeen) comes from Fakes.cs

namespace eThangAgent.AgentDomain.Tests;

/// <summary>The harness session id rides every provider request (OpenRouter sticky
///     sessions / prompt caching): the loop stamps <see cref="AgentOptions.SessionId"/>
///     onto each <see cref="ModelRequest"/> it builds, so the provider ACL can key
///     sticky routing on it. Absent wiring (legacy) leaves the request's id null —
///     byte-identical legacy behavior.</summary>
public class AgentSessionIdTests
{
  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", null, 100, 0.5f, 8192).Value!;

  [Fact]
  public async Task SendMessage_WithSessionIdOption_StampsEveryRequest()
  {
    AgentId sessionId = AgentId.NewId();
    FakeProvider provider = new(
        Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]),
        new AgentOptions { SessionId = sessionId.ToString() });

    _ = await agent.SendMessage("Hi", ct: TestContext.Current.CancellationToken);

    ModelRequest request = Assert.Single(provider.RequestsSeen);
    Assert.Equal(sessionId.ToString(), request.SessionId);
  }

  [Fact]
  public async Task SendMessage_WithoutSessionIdOption_RequestCarriesNull()
  {
    FakeProvider provider = new(
        Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]));

    _ = await agent.SendMessage("Hi", ct: TestContext.Current.CancellationToken);

    ModelRequest request = Assert.Single(provider.RequestsSeen);
    Assert.Null(request.SessionId);
  }

  [Fact]
  public async Task SendMessage_MultiIterationTurn_StampsEveryIteration()
  {
    AgentId sessionId = AgentId.NewId();
    // Two iterations: a tool call, then the final answer.
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("call_1", "echo", "{}")])),
        Result.Success(new ModelResponse("done", [])));
    FakeTool echo = new("echo", "echoed");
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([echo]),
        new AgentOptions { SessionId = sessionId.ToString() });

    _ = await agent.SendMessage("Hi", ct: TestContext.Current.CancellationToken);

    Assert.Equal(2, provider.RequestsSeen.Count);
    Assert.All(provider.RequestsSeen, r => Assert.Equal(sessionId.ToString(), r.SessionId));
  }
}
