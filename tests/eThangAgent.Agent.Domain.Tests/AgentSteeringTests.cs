using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Steering and interruption of the turn loop: inbox messages drain as User
/// messages at safe points (entry and iteration boundaries, never inside a tool batch),
/// and cancellation returns a repaired TurnCancelled failure instead of crashing.</summary>
public class AgentSteeringTests
{
  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", null, 100, 0.5f, 8192).Value!;

  /// <summary>Scripted provider capturing every request; falls back to a final plain answer.
  ///     <see cref="OnCall"/> runs with the 1-based call number BEFORE the response is
  ///     produced, which is how a test stages "the user typed while the model was streaming"
  ///     - the post lands in the inbox before the response returns.</summary>
  private sealed class ScriptedProvider(params ModelResponse[] responses) : IModelProvider
  {
    private readonly Queue<ModelResponse> _responses = new(responses);
    public List<ModelRequest> RequestsSeen { get; } = [];
    public int Calls => RequestsSeen.Count;
    public Action<int>? OnCall { get; set; }

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
    {
      // No defensive copy needed: the loop snapshots messages per request.
      RequestsSeen.Add(request);
      OnCall?.Invoke(RequestsSeen.Count);
      return Task.FromResult(Result.Success(
          _responses.Count > 0 ? _responses.Dequeue() : new ModelResponse("done", [])));
    }
  }

  /// <summary>Tool that runs an action when executed (e.g. posts steering or cancels the turn).</summary>
  private sealed class ActionTool(string name, Action run) : ITool
  {
    public ToolDefinition Definition { get; } = new ToolDefinition(name, "desc", []);

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
      run();
      return Task.FromResult(new ToolResult("ok", false));
    }
  }

  /// <summary>Regression pin for the live-view hazard: Conversation.Messages is a live
  /// wrapper over the growing list, so requests built from it directly would mutate after
  /// being sent. The loop must hand each provider call its own frozen copy.</summary>
  [Fact]
  public async Task CapturedRequest_IsFrozen_DoesNotMutateAsTurnContinues()
  {
    BoundedAgentMailbox inbox = new();
    ScriptedProvider provider = new(
        new ModelResponse(null, [new ToolCallRequest("call_1", "steer", "{}")]));
    ActionTool tool = new("steer", () => inbox.Post("later message"));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([tool]));

    _ = await agent.SendMessage("start", default, inbox: inbox, ct: TestContext.Current.CancellationToken);

    // The turn added a tool result, a steered user message, and a final answer after
    // request 1 was sent — none of it may appear in the captured first request.
    ModelRequest firstRequest = provider.RequestsSeen[0];
    _ = Assert.Single(firstRequest.Messages);
    Assert.Equal("start", firstRequest.Messages[0].Content);
    Assert.Equal(5, agent.Conversation.Messages.Count); // turn grew the conversation, not request 1
  }

  [Fact]
  public async Task SteeringPostedDuringToolRun_LandsInNextRequest_AsUserMessage()
  {
    BoundedAgentMailbox inbox = new();
    ScriptedProvider provider = new(
        new ModelResponse(null, [new ToolCallRequest("call_1", "steer", "{}")]),
        new ModelResponse("finished", []));
    ActionTool tool = new("steer", () => inbox.Post("also check the config file"));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([tool]));

    Result<string> result = await agent.SendMessage("start", default, inbox: inbox, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(2, provider.Calls);
    // The second request must carry the steered text as its newest message.
    IReadOnlyList<Message> secondRequestMessages = provider.RequestsSeen[1].Messages;
    Assert.Equal(Role.User, secondRequestMessages[^1].Role);
    Assert.Equal("also check the config file", secondRequestMessages[^1].Content);
  }

  /// <summary>Issue #253 B2: a message typed while the model STREAMS an answer (no tool
  ///     calls in the turn) must reach the model in the SAME turn. Before this, the loop
  ///     drained only at entry and iteration boundaries, and a pure-text turn has exactly
  ///     one iteration - so the steer sat queued until a later turn that might never come.
  ///     The safe point is after the response exists and BEFORE the loop decides the turn
  ///     is final: the assistant message is already appended, so the drained User message
  ///     follows it and no tool call is left dangling.</summary>
  [Fact]
  public async Task SteeringPostedWhileStreaming_ReachesTheModelInTheSameTurn()
  {
    BoundedAgentMailbox inbox = new();
    // The user types while the FIRST response is streaming: the post lands before the
    // response returns, which is exactly the mid-stream window the issue names.
    ScriptedProvider provider = new(
        new ModelResponse("first answer", []),
        new ModelResponse("second answer", []))
    {
      OnCall = call =>
      {
        if (call == 1)
        {
          inbox.Post("actually check the config");
        }
      },
    };
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([]));

    Result<string> result = await agent.SendMessage("start", default, inbox: inbox, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    // Two provider calls in ONE turn: the steer was not deferred to a later turn.
    Assert.Equal(2, provider.Calls);
    IReadOnlyList<Message> secondRequest = provider.RequestsSeen[1].Messages;
    Assert.Equal(Role.User, secondRequest[^1].Role);
    Assert.Equal("actually check the config", secondRequest[^1].Content);
    // Protocol invariant: the first answer is kept as an assistant message before the
    // steered user message - never a dangling tool call, never a dropped answer.
    Assert.Contains(secondRequest, m => m.Role == Role.Assistant && m.Content == "first answer");
    Assert.Equal("second answer", result.Value);
  }

  /// <summary>Issue #253 B3 depends on this: the drain fact is stamped with the LOOP's own
  ///     identity, which for a root is the session's persisted root id. The Desktop releases
  ///     a pinned steering echo only on a drain carrying that id (a child's drain names
  ///     messages the user never typed), so a fact stamped with anything else would leave
  ///     every echo pinned forever.</summary>
  [Fact]
  public async Task MidStreamDrain_PublishesTheFact_StampedWithTheLoopsOwnId()
  {
    BoundedAgentMailbox inbox = new();
    RecordingEvents events = new();
    ScriptedProvider provider = new(
        new ModelResponse("first answer", []),
        new ModelResponse("second answer", []))
    {
      OnCall = call =>
      {
        if (call == 1)
        {
          inbox.Post("steer mid-stream");
        }
      },
    };
    AgentId agentId = AgentId.NewId();
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]),
        new AgentOptions { Id = agentId, Events = events });
    List<MailboxDrainedEvent> drained = [];
    using IDisposable lease = events.Subscribe(new DrainRecorder(drained));

    _ = await agent.SendMessage("start", default, inbox: inbox, ct: TestContext.Current.CancellationToken);

    MailboxDrainedEvent fact = Assert.Single(drained);
    Assert.Equal(agentId, fact.ChildId);
    _ = Assert.Single(fact.MessageIds);
  }

  /// <summary>A minimal in-memory event stream: the domain test project references the
  ///     Domain only, and a domain test must use fakes anyway.</summary>
  private sealed class RecordingEvents : IAgentEvents
  {
    private readonly List<IAgentEventSubscriber> _subscribers = [];

    public IDisposable Subscribe(IAgentEventSubscriber subscriber)
    {
      _subscribers.Add(subscriber);
      return new Lease(this, subscriber);
    }

    public void Publish(ChildEvent evt)
    {
      foreach (IAgentEventSubscriber subscriber in _subscribers.ToList())
      {
        subscriber.OnEvent(evt);
      }
    }

    private sealed class Lease(RecordingEvents owner, IAgentEventSubscriber subscriber) : IDisposable
    {
      public void Dispose() => _ = owner._subscribers.Remove(subscriber);
    }
  }

  /// <summary>Collects the mailbox drain facts a loop publishes.</summary>
  private sealed class DrainRecorder(List<MailboxDrainedEvent> into) : IAgentEventSubscriber
  {
    public void OnEvent(ChildEvent evt)
    {
      if (evt is MailboxDrainedEvent fact)
      {
        into.Add(fact);
      }
    }
  }

  /// <summary>Issue #253 B2 (bound): the mid-stream drain does not loop forever. A model
  ///     that answers without tool calls while the user keeps typing must still terminate -
  ///     the drain continues the turn only while a message was actually consumed.</summary>
  [Fact]
  public async Task SteeringDrainedMidStream_DoesNotContinueTheTurnWhenNothingWasQueued()
  {
    BoundedAgentMailbox inbox = new();
    ScriptedProvider provider = new(new ModelResponse("only answer", []));
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]));

    Result<string> result = await agent.SendMessage("start", default, inbox: inbox, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("only answer", result.Value);
    Assert.Equal(1, provider.Calls); // no empty continuation loop
  }

  [Fact]
  public async Task LeftoverSteering_DrainsAtTurnEntry_BeforeNewUserText()
  {
    BoundedAgentMailbox inbox = new();
    inbox.Post("queued earlier");
    ScriptedProvider provider = new();
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]));

    Result<string> result = await agent.SendMessage("fresh question", default, inbox: inbox, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    IReadOnlyList<Message> first = provider.RequestsSeen[0].Messages;
    // Newest message at request time must be the fresh text, preceded by the leftover.
    Assert.Equal("fresh question", first[^1].Content);
    Assert.Equal("queued earlier", first[^2].Content);
    // And the durable conversation itself carries both, in order, ahead of the answer.
    IReadOnlyList<Message> messages = agent.Conversation.Messages;
    Assert.Equal("queued earlier", messages[0].Content);
    Assert.Equal("fresh question", messages[1].Content);
    Assert.Equal(Role.Assistant, messages[2].Role);
  }

  [Fact]
  public async Task SteeringNeverSplits_ToolCallFromItsResults()
  {
    BoundedAgentMailbox inbox = new();
    ScriptedProvider provider = new(
        new ModelResponse("thinking", [new ToolCallRequest("call_1", "steer", "{}")]));
    ActionTool tool = new("steer", () => inbox.Post("mid-batch"));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([tool]));

    _ = await agent.SendMessage("go", default, inbox: inbox, ct: TestContext.Current.CancellationToken);

    // Drained after the tool batch: tool result precedes the steered user message.
    IReadOnlyList<Message> messages = agent.Conversation.Messages;
    int toolResultIndex = messages.Select((m, i) => (m, i))
        .First(x => x.m.Role == Role.Tool).i;
    Assert.Equal(Role.User, messages[toolResultIndex + 1].Role);
    Assert.Equal("mid-batch", messages[toolResultIndex + 1].Content);
  }

  [Fact]
  public async Task CancellationMidToolBatch_FailsWithTurnCancelled_AndRepairsDanglingCalls()
  {
    using CancellationTokenSource cts = new();
    ScriptedProvider provider = new(
        new ModelResponse(null,
            [new ToolCallRequest("call_1", "boom", "{}"),
                 new ToolCallRequest("call_2", "boom", "{}")]));
    ActionTool boom = new("boom", () =>
    {
      cts.Cancel();
      cts.Token.ThrowIfCancellationRequested(); // tools observe ct like real work does
    });
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([boom]));

    Result<string> result = await agent.SendMessage("go", ct: cts.Token);

    Assert.False(result.IsSuccess);
    Assert.Equal(Agent.TurnCancelledCode, result.Error.Code);
    // Every unanswered tool call received the synthetic interrupted result.
    List<Message> trailing = [.. agent.Conversation.Messages.TakeLast(3)];
    List<Message> toolResults = [.. trailing.Where(m => m.Role == Role.Tool)];
    Assert.Equal(2, toolResults.Count);
    Assert.All(toolResults, m => Assert.Equal(Agent.InterruptedToolResult, m.Content));
    Assert.Contains(toolResults, m => m.ToolCallId == "call_1");
    Assert.Contains(toolResults, m => m.ToolCallId == "call_2");
  }

  [Fact]
  public async Task NoInbox_TurnBehavesExactlyAsBefore()
  {
    ScriptedProvider provider = new(new ModelResponse("plain answer", []));
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]));

    Result<string> result = await agent.SendMessage("hi", default, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("plain answer", result.Value);
  }
}
