using eThangAgent.AgentDomain;
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
        ModelConfig.Create("test-model", 100, 0.5f).Value!;

    /// <summary>Scripted provider capturing every request; falls back to a final plain answer.</summary>
    private sealed class ScriptedProvider(params ModelResponse[] responses) : IModelProvider
    {
        private readonly Queue<ModelResponse> _responses = new(responses);
        public List<ModelRequest> RequestsSeen { get; } = [];
        public int Calls => RequestsSeen.Count;

        public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
            CancellationToken ct = default)
        {
            // No defensive copy needed: the loop snapshots messages per request.
            RequestsSeen.Add(request);
            return Task.FromResult(Result<ModelResponse>.Success(
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
        var inbox = new AgentInbox();
        var provider = new ScriptedProvider(
            new ModelResponse(null, [new ToolCallRequest("call_1", "steer", "{}")]));
        var tool = new ActionTool("steer", () => inbox.Post("later message"));
        var agent = new Agent(provider, new Conversation(), DefaultConfig,
            new ToolRegistry([tool]));

        await agent.SendMessage("start", default, inbox: inbox);

        // The turn added a tool result, a steered user message, and a final answer after
        // request 1 was sent — none of it may appear in the captured first request.
        var firstRequest = provider.RequestsSeen[0];
        Assert.Single(firstRequest.Messages);
        Assert.Equal("start", firstRequest.Messages[0].Content);
        Assert.Equal(5, agent.Conversation.Messages.Count); // turn grew the conversation, not request 1
    }

    [Fact]
    public async Task SteeringPostedDuringToolRun_LandsInNextRequest_AsUserMessage()
    {
        var inbox = new AgentInbox();
        var provider = new ScriptedProvider(
            new ModelResponse(null, [new ToolCallRequest("call_1", "steer", "{}")]),
            new ModelResponse("finished", []));
        var tool = new ActionTool("steer", () => inbox.Post("also check the config file"));
        var agent = new Agent(provider, new Conversation(), DefaultConfig,
            new ToolRegistry([tool]));

        var result = await agent.SendMessage("start", default, inbox: inbox);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, provider.Calls);
        // The second request must carry the steered text as its newest message.
        var secondRequestMessages = provider.RequestsSeen[1].Messages;
        Assert.Equal(Role.User, secondRequestMessages[^1].Role);
        Assert.Equal("also check the config file", secondRequestMessages[^1].Content);
    }

    [Fact]
    public async Task LeftoverSteering_DrainsAtTurnEntry_BeforeNewUserText()
    {
        var inbox = new AgentInbox();
        inbox.Post("queued earlier");
        var provider = new ScriptedProvider();
        var agent = new Agent(provider, new Conversation(), DefaultConfig, new ToolRegistry([]));

        var result = await agent.SendMessage("fresh question", default, inbox: inbox);

        Assert.True(result.IsSuccess);
        var first = provider.RequestsSeen[0].Messages;
        // Newest message at request time must be the fresh text, preceded by the leftover.
        Assert.Equal("fresh question", first[^1].Content);
        Assert.Equal("queued earlier", first[^2].Content);
        // And the durable conversation itself carries both, in order, ahead of the answer.
        var messages = agent.Conversation.Messages;
        Assert.Equal("queued earlier", messages[0].Content);
        Assert.Equal("fresh question", messages[1].Content);
        Assert.Equal(Role.Assistant, messages[2].Role);
    }

    [Fact]
    public async Task SteeringNeverSplits_ToolCallFromItsResults()
    {
        var inbox = new AgentInbox();
        var provider = new ScriptedProvider(
            new ModelResponse("thinking", [new ToolCallRequest("call_1", "steer", "{}")]));
        var tool = new ActionTool("steer", () => inbox.Post("mid-batch"));
        var agent = new Agent(provider, new Conversation(), DefaultConfig,
            new ToolRegistry([tool]));

        await agent.SendMessage("go", default, inbox: inbox);

        // Drained after the tool batch: tool result precedes the steered user message.
        var messages = agent.Conversation.Messages;
        var toolResultIndex = messages.Select((m, i) => (m, i))
            .First(x => x.m.Role == Role.Tool).i;
        Assert.Equal(Role.User, messages[toolResultIndex + 1].Role);
        Assert.Equal("mid-batch", messages[toolResultIndex + 1].Content);
    }

    [Fact]
    public async Task CancellationMidToolBatch_FailsWithTurnCancelled_AndRepairsDanglingCalls()
    {
        var cts = new CancellationTokenSource();
        var provider = new ScriptedProvider(
            new ModelResponse(null,
                [new ToolCallRequest("call_1", "boom", "{}"),
                 new ToolCallRequest("call_2", "boom", "{}")]));
        var boom = new ActionTool("boom", () =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested(); // tools observe ct like real work does
        });
        var agent = new Agent(provider, new Conversation(), DefaultConfig,
            new ToolRegistry([boom]));

        var result = await agent.SendMessage("go", cts.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(Agent.TurnCancelledCode, result.Error!.Code);
        // Every unanswered tool call received the synthetic interrupted result.
        var trailing = agent.Conversation.Messages.TakeLast(3).ToList();
        var toolResults = trailing.Where(m => m.Role == Role.Tool).ToList();
        Assert.Equal(2, toolResults.Count);
        Assert.All(toolResults, m => Assert.Equal(Agent.InterruptedToolResult, m.Content));
        Assert.Contains(toolResults, m => m.ToolCallId == "call_1");
        Assert.Contains(toolResults, m => m.ToolCallId == "call_2");
    }

    [Fact]
    public async Task NoInbox_TurnBehavesExactlyAsBefore()
    {
        var provider = new ScriptedProvider(new ModelResponse("plain answer", []));
        var agent = new Agent(provider, new Conversation(), DefaultConfig, new ToolRegistry([]));

        var result = await agent.SendMessage("hi", default);

        Assert.True(result.IsSuccess);
        Assert.Equal("plain answer", result.Value);
    }
}
