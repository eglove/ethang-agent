using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

// Pins for the reasoning TELEMETRY channel at the loop (issue #263): the trace is
// recorded at the safe point each iteration completes, under CancellationToken.None, and
// a persistence fault never fails the turn. Nothing here feeds back into a request.
public class AgentReasoningTelemetryTests
{
  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", null, 100, 0.5f, 8192).Value!;

  /// <summary>Scripted provider reporting a reasoning trace per response.</summary>
  private sealed class ReasoningProvider(params Result<ModelResponse>[] responses) : IModelProvider
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
  public async Task SendMessage_WithReasoning_RecordsOneEventPerIteration()
  {
    ReasoningProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("c1", "read", "{}")])
        {
          Reasoning = "first thought",
        }),
        Result.Success(new ModelResponse("done", [])
        {
          Reasoning = "second thought",
        }));
    RecordingEventLog log = new();
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new FakeTool("read", "content")]),
        new AgentOptions { EventLog = log });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    Assert.Equal(2, log.Events.Count);
    AgentReasoningRecorded first = Assert.IsType<AgentReasoningRecorded>(log.Events[0]);
    AgentReasoningRecorded second = Assert.IsType<AgentReasoningRecorded>(log.Events[1]);
    Assert.Equal("first thought", first.Text);
    Assert.Equal("second thought", second.Text);
    // (agent, turn, iteration) identity: one turn id across the turn, 0-based iteration.
    Assert.Equal(first.TurnId, second.TurnId);
    Assert.Equal(0, first.Iteration);
    Assert.Equal(1, second.Iteration);
    Assert.Equal(agent.Id, first.AgentId);
  }

  [Fact]
  public async Task SendMessage_WithoutReasoning_RecordsNothing()
  {
    ReasoningProvider provider = new(Result.Success(new ModelResponse("done", [])));
    RecordingEventLog log = new();
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]),
        new AgentOptions { EventLog = log });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    Assert.Empty(log.Events);
  }

  [Fact]
  public async Task SendMessage_EventLogFault_DoesNotFailTheTurn()
  {
    ReasoningProvider provider = new(Result.Success(new ModelResponse("answered", [])
    {
      Reasoning = "a thought",
    }));
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]),
        new AgentOptions { EventLog = new RecordingEventLog(fault: true) });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    // Best-effort telemetry: the turn completes normally despite the store fault.
    Assert.True(result.IsSuccess);
    Assert.Equal("answered", result.Value);
  }

  [Fact]
  public async Task SendMessage_ReasoningPersisted_LeavesTheNextRequestByteIdentical()
  {
    // The context channel is provably independent of the telemetry channel: the two
    // runs differ ONLY in whether an event log is wired, so every request the provider
    // saw must be identical.
    static ReasoningProvider Provider() => new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("c1", "read", "{}")])
        {
          Reasoning = "trace text",
        }),
        Result.Success(new ModelResponse("done", []) { Reasoning = "more trace" }));

    List<ModelRequest> withoutLog = [];
    Agent plain = new(Capturing(Provider(), withoutLog), new Conversation(), DefaultConfig,
        new ToolRegistry([new FakeTool("read", "content")]));
    _ = await plain.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    List<ModelRequest> withLog = [];
    Agent traced = new(Capturing(Provider(), withLog), new Conversation(), DefaultConfig,
        new ToolRegistry([new FakeTool("read", "content")]),
        new AgentOptions { EventLog = new RecordingEventLog() });
    _ = await traced.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal(withoutLog.Count, withLog.Count);
    for (int i = 0; i < withoutLog.Count; i++)
    {
      Assert.Equal(Messages(withoutLog[i]), Messages(withLog[i]));
    }
  }

  /// <summary>Wraps a provider so every request it receives is captured in order.</summary>
  private static CapturingProvider Capturing(IModelProvider inner, List<ModelRequest> seen)
      => new(inner, seen);

  /// <summary>A request's message list rendered as comparable text.</summary>
  private static string Messages(ModelRequest request)
      => string.Join("\n", request.Messages.Select(m =>
          $"{m.Role}|{m.Content}|{string.Join(",", (m.ToolCalls ?? []).Select(c => c.Id + ":" + c.Name + ":" + c.Arguments))}"));

  private sealed class CapturingProvider(IModelProvider inner, List<ModelRequest> seen) : IModelProvider
  {
    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
    {
      seen.Add(request);
      return inner.SendAsync(config, request, ct);
    }
  }
}
