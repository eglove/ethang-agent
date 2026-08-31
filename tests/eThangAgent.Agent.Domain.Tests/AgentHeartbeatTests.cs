using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

public class AgentHeartbeatTests
{
  private static ModelConfig Config() =>
      ModelConfig.Create("test-model", null, 1024, 0.5f, 8192).Value!;

  private static AgentOptions Options(IAgentHeartbeat heartbeat) => new() { Heartbeat = heartbeat };

  [Fact]
  public async Task SendMessage_WithoutHeartbeat_BehavesIdentically()
  {
    // Absent-collaborator rule: no heartbeat wired, plain fake provider -> normal turn.
    Agent agent = new(new FakeProvider(Result.Success(new ModelResponse("done", []))),
        new Conversation(), Config(), new ToolRegistry([new FakeTool("t", "ok")]));

    Result<string> run = await agent.SendMessage("hello", ct: TestContext.Current.CancellationToken);

    Assert.True(run.IsSuccess);
  }

  [Fact]
  public async Task SendMessage_WithHeartbeat_BeatsEveryIteration()
  {
    CountingHeartbeat heartbeat = new();
    // LoopingProvider answers every iteration with one more tool call; the turn only
    // ends when the token fires. The 200 ms timer is the reachable completion path
    // (deadlock vigilance): CTS fires at 200 ms, ThrowIfCancellationRequested throws,
    // SendMessage returns Failure(TurnCancelled).
    using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(200));

    Agent agent = new(new LoopingProvider(), new Conversation(), Config(),
        new ToolRegistry([new FakeTool("loop", "ok")]), Options(heartbeat));

    Result<string> run = await agent.SendMessage("go", ct: cts.Token);

    Assert.True(heartbeat.BeatCount >= 1);
    Assert.False(run.IsSuccess); // the 200 ms timer is what ended the turn
  }

  [Fact]
  public async Task ToolExecution_BeatsBeforeAndAfter()
  {
    CountingHeartbeat heartbeat = new();
    // One tool-call response, then a final answer.
    Agent agent = new(
        new FakeProvider(
            Result.Success(new ModelResponse(null,
                [new ToolCallRequest("call_1", "t", "{}")])),
            Result.Success(new ModelResponse("done", []))),
        new Conversation(), Config(),
        new ToolRegistry([new FakeTool("t", "ok")]), Options(heartbeat));

    Result<string> run = await agent.SendMessage("use the tool", ct: TestContext.Current.CancellationToken);

    Assert.True(run.IsSuccess);
    // Beats: turn start, per iteration top, and around the tool call.
    Assert.True(heartbeat.BeatCount >= 3);
  }

  [Fact]
  public async Task SendMessage_WithHeartbeat_NeverThrowsWhenHeartbeatIsNullSafe()
  {
    // Options present but Heartbeat left at its default (null): the loop must not throw.
    Agent agent = new(new FakeProvider(Result.Success(new ModelResponse("done", []))),
        new Conversation(), Config(), new ToolRegistry([new FakeTool("t", "ok")]),
        new AgentOptions());

    Result<string> run = await agent.SendMessage("hello", ct: TestContext.Current.CancellationToken);

    Assert.True(run.IsSuccess);
  }

  private sealed class CountingHeartbeat : IAgentHeartbeat
  {
    public int BeatCount { get; private set; }

    public void Beat(AgentId agentId) => BeatCount++;

    public bool TryGetLastBeat(AgentId agentId, out DateTimeOffset lastBeat)
    {
      lastBeat = DateTimeOffset.UtcNow;
      return BeatCount > 0;
    }

    public void Forget(AgentId agentId)
    {
    }
  }
}
