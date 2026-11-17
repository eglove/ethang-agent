using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

public class AgentHeartbeatTests
{
  private static ModelConfig Config() =>
      ModelConfig.Create("test-model", 1024, 0.5f, 8192).Value!;

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
    // LoopingProvider answers every iteration with one more tool call, so the turn would run
    // until the token fires. The turn ends on a FACT the test controls: the heartbeat cancels its
    // own token on the third beat, so the run stops on an iteration boundary the test chose and no
    // duration decides when the turn ends.
    using CancellationTokenSource cts = new();
    CountingHeartbeat heartbeat = new(cancelOnBeat: 3, cts);

    Agent agent = new(new LoopingProvider(), new Conversation(), Config(),
        new ToolRegistry([new FakeTool("loop", "ok")]), Options(heartbeat));

    Result<string> run = await agent.SendMessage("go", ct: cts.Token)
        .WaitAsync(TestCeiling.Value, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(heartbeat.BeatCount >= 3);
    Assert.False(run.IsSuccess); // the cancelled token is what ended the turn
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

  private sealed class CountingHeartbeat(int? cancelOnBeat, CancellationTokenSource? cts) : IAgentHeartbeat
  {
    public CountingHeartbeat() : this(null, null)
    {
    }

    public int BeatCount { get; private set; }

    public void Beat(AgentId agentId)
    {
      BeatCount++;
      if (cancelOnBeat is { } beat && BeatCount >= beat)
      {
        cts!.Cancel();
      }
    }

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
