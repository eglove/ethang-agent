using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>FR-C2 bridge (the gap the R5 review found): agent.send delivers into the
///     runtime's mailbox AND the running child actually drains it at its next safe
///     point — the steered text reaches the child's provider request as a User message.
///     This pins the SubAgentServices.InboxFor wiring the composition installs.</summary>
public class InProcessSteeringBridgeTests
{
  private static AgentRecord Child() => AgentRecord.Spawned(AgentId.NewId(), null, 1, "m/sub",
      "steered", "start", new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc));

  [Fact]
  public async Task InboxFromServices_DrainsIntoTheChildsNextRequest()
  {
    // The runtime's mailbox for the child; the spawner resolves the SAME instance
    // through SubAgentServices.InboxFor — exactly what the composition wires.
    BoundedAgentMailbox mailbox = new();
    _ = mailbox.Deliver(new PendingMessage("steer: check the file", MessageUrgency.Normal,
        DateTimeOffset.UtcNow, "parent"));

    FakeAgentStore store = new();
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("c1", "read", "{}")])),
        Result.Success(new ModelResponse("done", [])));
    SubAgentSpawner spawner = new(new SubAgentServices(
        new FakeModelProviderFactory(provider), store,
        new ToolRegistry([new FakeTool("read", "ok")]),
        new StaticPromptProvider("guide"),
        new SubAgentOptions(DefaultModel: "m/sub"),
        InboxFor: _ => mailbox));

    AgentRunOutcome outcome = await spawner.RunAsync(Child(), TestContext.Current.CancellationToken);

    Assert.Equal(AgentStatus.Completed, outcome.Status);
    // The steered text was drained at a safe point and reached the child's second
    // provider request as a User message.
    Assert.Contains(provider.RequestsSeen, req => req.Messages.Any(m =>
        m.Role == ConversationDomain.Role.User && m.Content.Contains("steer: check the file", StringComparison.Ordinal)));
  }

  [Fact]
  public async Task NoInboxFor_ChildRunsUnsteered_LegacyShape()
  {
    FakeAgentStore store = new();
    FakeProvider provider = new(Result.Success(new ModelResponse("done", [])));
    SubAgentSpawner spawner = new(new SubAgentServices(
        new FakeModelProviderFactory(provider), store,
        new ToolRegistry([]),
        new StaticPromptProvider("guide"),
        new SubAgentOptions(DefaultModel: "m/sub")));

    AgentRunOutcome outcome = await spawner.RunAsync(Child(), TestContext.Current.CancellationToken);

    Assert.Equal(AgentStatus.Completed, outcome.Status);
  }
}
