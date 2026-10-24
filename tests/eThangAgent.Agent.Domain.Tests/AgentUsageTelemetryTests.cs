using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

// Pins for the usage TELEMETRY channel at the loop (issue #270): one row per completed
// provider call, keyed (agent, turn, iteration) so it aligns with that turn's reasoning
// rows, best-effort so a fault never fails the turn, and carrying NULL for an unreported
// count rather than a fabricated zero.
public class AgentUsageTelemetryTests
{
  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", null, 100, 0.5f, 8192).Value!;

  /// <summary>Scripted provider returning one response per call.</summary>
  private sealed class ScriptedProvider(params Result<ModelResponse>[] responses) : IModelProvider
  {
    private readonly Queue<Result<ModelResponse>> _responses = new(responses);

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
        => Task.FromResult(_responses.Count > 0
            ? _responses.Dequeue()
            : Result.Success(new ModelResponse("done", [])));
  }

  /// <summary>Recording event log; optionally faults every append.</summary>
  private sealed class RecordingEventLog(bool fault = false) : IAgentEventLog
  {
    public List<AgentDomainEvent> Events { get; } = [];

    public Task<Result<string>> AppendEventAsync(AgentDomainEvent domainEvent, CancellationToken ct = default)
    {
      if (fault)
      {
        throw new InvalidOperationException("store exploded");
      }

      Events.Add(domainEvent);
      return Task.FromResult(Result.Success(domainEvent.AgentId.ToString()));
    }
  }

  [Fact]
  public async Task SendMessage_WithUsage_RecordsOneRowPerProviderCall()
  {
    ScriptedProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("c1", "read", "{}")])
        {
          Usage = new TokenUsage(100, 20, 64, null, 7),
        }),
        Result.Success(new ModelResponse("done", [])
        {
          Usage = new TokenUsage(150, 30, 128, null, 11),
        }));
    RecordingEventLog log = new();
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new FakeTool("read", "content")]),
        new AgentOptions { EventLog = log });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    List<AgentUsageRecorded> usage = [.. log.Events.OfType<AgentUsageRecorded>()];
    Assert.Equal(2, usage.Count);
    Assert.Equal(100, usage[0].InputTokens);
    Assert.Equal(20, usage[0].OutputTokens);
    Assert.Equal(64, usage[0].CachedInputTokens);
    Assert.Equal(7, usage[0].ReasoningTokens);
    Assert.Equal(150, usage[1].InputTokens);
    Assert.Equal(0, usage[0].Iteration);
    Assert.Equal(1, usage[1].Iteration);
    // One turn id across the turn, and the agent's own id on every row.
    Assert.Equal(usage[0].TurnId, usage[1].TurnId);
    Assert.Equal(agent.Id, usage[0].AgentId);
  }

  [Fact]
  public async Task SendMessage_UnreportedCounts_AreNullNotZero()
  {
    // The #262 rule at the persistence boundary: a provider that reported neither count
    // stores NULL, so it stays distinguishable from one reporting 0.
    ScriptedProvider provider = new(
        Result.Success(new ModelResponse("done", [])
        {
          Usage = new TokenUsage(100, 20),
        }));
    RecordingEventLog log = new();
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]),
        new AgentOptions { EventLog = log });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    AgentUsageRecorded row = Assert.Single(log.Events.OfType<AgentUsageRecorded>());
    Assert.Null(row.CachedInputTokens);
    Assert.Null(row.ReasoningTokens);
  }

  [Fact]
  public async Task SendMessage_ReportedZeroCounts_AreDistinctFromUnreported()
  {
    ScriptedProvider provider = new(
        Result.Success(new ModelResponse("done", [])
        {
          Usage = new TokenUsage(100, 20, 0, null, 0),
        }));
    RecordingEventLog log = new();
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]),
        new AgentOptions { EventLog = log });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    AgentUsageRecorded row = Assert.Single(log.Events.OfType<AgentUsageRecorded>());
    Assert.Equal(0, row.CachedInputTokens);
    Assert.Equal(0, row.ReasoningTokens);
  }

  [Fact]
  public async Task SendMessage_WithoutUsageReport_RecordsNoUsageRow()
  {
    ScriptedProvider provider = new(Result.Success(new ModelResponse("done", [])));
    RecordingEventLog log = new();
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]),
        new AgentOptions { EventLog = log });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    Assert.Empty(log.Events.OfType<AgentUsageRecorded>());
  }

  [Fact]
  public async Task SendMessage_FaultingEventLog_NeverFailsTheTurn()
  {
    ScriptedProvider provider = new(Result.Success(new ModelResponse("done", [])
    {
      Usage = new TokenUsage(100, 20, 64, null, 7),
      Reasoning = "a thought",
    }));
    RecordingEventLog log = new(fault: true);
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]),
        new AgentOptions { EventLog = log });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    Assert.Equal("done", result.Value);
  }

  [Fact]
  public async Task SendMessage_UsageAndReasoningRows_ShareTurnAndIterationIdentity()
  {
    // The alignment trap: recording usage from ReportUsage would land it after
    // state.Iteration++ and number it one ahead of the reasoning row for the same call.
    ScriptedProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("c1", "read", "{}")])
        {
          Usage = new TokenUsage(100, 20, 64, null, 7),
          Reasoning = "first thought",
        }),
        Result.Success(new ModelResponse("done", [])
        {
          Usage = new TokenUsage(150, 30, 128, null, 11),
          Reasoning = "second thought",
        }));
    RecordingEventLog log = new();
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new FakeTool("read", "content")]),
        new AgentOptions { EventLog = log });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    List<AgentReasoningRecorded> reasoning = [.. log.Events.OfType<AgentReasoningRecorded>()];
    List<AgentUsageRecorded> usage = [.. log.Events.OfType<AgentUsageRecorded>()];
    Assert.Equal(2, reasoning.Count);
    Assert.Equal(2, usage.Count);
    for (int i = 0; i < reasoning.Count; i++)
    {
      Assert.Equal(reasoning[i].TurnId, usage[i].TurnId);
      Assert.Equal(reasoning[i].Iteration, usage[i].Iteration);
    }
  }
}
