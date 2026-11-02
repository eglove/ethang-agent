using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Child wiring: each child run stamps the child's OWN persisted id onto its
///     provider requests — one OpenRouter sticky session per child conversation, and
///     the child host (remote mode) inherits the same stamping through the same
///     spawner. Legacy wiring without an explicit id falls back to the record id,
///     which IS the child's persisted id.</summary>
public class AgentRunLoopSessionIdTests
{
  private static readonly DateTimeOffset FixedNow = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

  private static AgentRecord Child(string model = "default/sub-model")
      => AgentRecord.Started(AgentId.NewId(), model, null, "do things", FixedNow);

  [Fact]
  public async Task RunAsync_RequestsCarryTheChildsOwnId()
  {
    FakeAgentStore store = new();
    FakeProvider provider = new(Result.Success(new ModelResponse("report", [])));
    AgentRunLoop spawner = new(new AgentRunServices(
        new FakeModelProviderFactory(provider), store, new ToolRegistry([]),
        new StaticPromptProvider("guide text"), new AgentRunOptions(DefaultModel: "default/sub-model")));

    AgentRecord child = Child();
    _ = await spawner.RunAsync(child, CancellationToken.None);

    ModelRequest request = Assert.Single(provider.RequestsSeen);
    Assert.Equal(child.Id.ToString(), request.SessionId);
  }
}
