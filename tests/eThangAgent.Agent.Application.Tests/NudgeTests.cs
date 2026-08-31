using eThangAgent.Agent.Application.Nudges;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Ag = eThangAgent.AgentDomain.Agent;

namespace eThangAgent.Agent.Application.Tests;

public class NudgeTests
{
  private const string ExpectedNudgeLine =
      "[nudge] This turn involved several tools and nothing has been saved to curated memories yet. " +
      "If any durable convention, preference, insight, failure, or reference emerged, consider " +
      "memories.add (search first - near-duplicate adds are rejected); memories.purge drops entries you no " +
      "longer trust - otherwise continue.";


  [Fact]
  public void ReminderLine_MentionsPruningStaleMemories()
  => Assert.Contains("memories.purge", DefaultNudgePolicy.ReminderLine, StringComparison.Ordinal);
  // ---- DefaultNudgePolicy ----

  [Fact]
  public void Evaluate_AllConditionsHold_ReturnsVerbatimLine()
  {
    DefaultNudgePolicy policy = new();

    string? line = policy.Evaluate(new NudgeContext(TurnNumber: 5, LastToolCalls: 3, MemoriesWrittenTotal: 0));

    Assert.Equal(ExpectedNudgeLine, line);
  }

  [Theory]
  [InlineData(5, 3, 0, true)]   // all three conditions hold
  [InlineData(10, 3, 0, true)]  // later multiple still fires
  [InlineData(25, 9, 0, true)]  // larger values are fine
  [InlineData(4, 3, 0, false)]  // not a multiple of 5 yet
  [InlineData(6, 3, 0, false)]  // past the multiple — silent until the next (modulo cooldown)
  [InlineData(9, 5, 0, false)]  // still within the quiet window
  [InlineData(5, 2, 0, false)]  // too few tool calls
  [InlineData(5, 0, 0, false)]  // no tool calls at all
  [InlineData(5, 3, 1, false)]  // already wrote a memory this session
  [InlineData(5, 3, 4, false)]  // any nonzero write count silences
  [InlineData(7, 2, 2, false)]  // nothing holds
  public void Evaluate_TruthTable_FiresOnlyWhenAllConditionsHold(
      int turnNumber, int lastToolCalls, int memoriesWritten, bool fires)
  {
    DefaultNudgePolicy policy = new();

    string? line = policy.Evaluate(new NudgeContext(turnNumber, lastToolCalls, memoriesWritten));

    if (fires)
    {
      Assert.Equal(ExpectedNudgeLine, line);
    }
    else
    {
      Assert.Null(line);
    }
  }

  // ---- SendMessageCommandHandler integration ----

  [Fact]
  public async Task Handle_PolicyReturnsLine_AppendsSystemMessageAfterSuccessfulTurn()
  {
    ScriptedProvider provider = new(Result.Success(new ModelResponse("ok", [])));
    (Ag? agent, Conversation? conversation) = BuildAgent(provider);
    CountingPolicy policy = new("[nudge] remember to curate");
    SendMessageCommandHandler handler = new(agent, conversation, policy, () => 0);

    Result<string> result = await handler.Handle(new SendMessageCommand("hello"), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(3, conversation.Messages.Count);
    Message system = conversation.Messages[2];
    Assert.Equal(Role.System, system.Role);
    Assert.Equal("[nudge] remember to curate", system.Content);
  }

  [Fact]
  public async Task Handle_PolicySilent_NothingAppended()
  {
    ScriptedProvider provider = new(Result.Success(new ModelResponse("ok", [])));
    (Ag? agent, Conversation? conversation) = BuildAgent(provider);
    CountingPolicy policy = new(null);
    SendMessageCommandHandler handler = new(agent, conversation, policy, () => 0);

    _ = await handler.Handle(new SendMessageCommand("hello"), ct: TestContext.Current.CancellationToken);

    Assert.Equal(2, conversation.Messages.Count);
    Assert.Equal(Role.User, conversation.Messages[0].Role);
    Assert.Equal(Role.Assistant, conversation.Messages[1].Role);
  }

  [Fact]
  public async Task Handle_ProviderFailure_NeverEvaluatesPolicyOrAppends()
  {
    DomainError error = new("FAIL", "provider down");
    ScriptedProvider provider = new(Result.Failure<ModelResponse>(error));
    (Ag? agent, Conversation? conversation) = BuildAgent(provider);
    CountingPolicy policy = new("[nudge] should not appear");
    SendMessageCommandHandler handler = new(agent, conversation, policy, () => 0);

    Result<string> result = await handler.Handle(new SendMessageCommand("hello"), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal(error, result.Error);
    Assert.Empty(policy.ContextsSeen);
    _ = Assert.Single(conversation.Messages); // only the user message
  }

  [Fact]
  public async Task Handle_TurnCounterIncrementsAcrossInvocations()
  {
    ScriptedProvider provider = new(
        Result.Success(new ModelResponse("one", [])),
        Result.Success(new ModelResponse("two", [])),
        Result.Success(new ModelResponse("three", [])));
    (Ag? agent, Conversation? conversation) = BuildAgent(provider);
    CountingPolicy policy = new(null);
    SendMessageCommandHandler handler = new(agent, conversation, policy, () => 0);

    _ = await handler.Handle(new SendMessageCommand("a"), ct: TestContext.Current.CancellationToken);
    _ = await handler.Handle(new SendMessageCommand("b"), ct: TestContext.Current.CancellationToken);
    _ = await handler.Handle(new SendMessageCommand("c"), ct: TestContext.Current.CancellationToken);

    Assert.Equal([1, 2, 3], policy.ContextsSeen.Select(c => c.TurnNumber));
  }

  [Fact]
  public async Task Handle_PassesToolCallCountAndTrackerTotalToPolicy()
  {
    ScriptedProvider provider = new(
        Result.Success(new ModelResponse(null,
        [
            new ToolCallRequest("c1", "t", "{}"),
                new ToolCallRequest("c2", "t", "{}"),
                new ToolCallRequest("c3", "t", "{}"),
        ])),
        Result.Success(new ModelResponse("done", [])));
    Conversation conversation = new();
    Ag agent = new(provider, conversation,
        ModelConfig.Create("m", null, 100, 0.5f, 8192).Value!,
        new ToolRegistry([new StubTool("t", "ok")]));
    CountingPolicy policy = new(null);
    SendMessageCommandHandler handler = new(agent, conversation, policy, () => 7);

    _ = await handler.Handle(new SendMessageCommand("hello"), ct: TestContext.Current.CancellationToken);

    NudgeContext context = Assert.Single(policy.ContextsSeen);
    Assert.Equal(1, context.TurnNumber);
    Assert.Equal(3, context.LastToolCalls);
    Assert.Equal(7, context.MemoriesWrittenTotal);
  }

  [Fact]
  public async Task Handle_PolicySuppliedWithoutCounter_NudgingStaysOff()
  {
    ScriptedProvider provider = new(Result.Success(new ModelResponse("ok", [])));
    (Ag? agent, Conversation? conversation) = BuildAgent(provider);
    CountingPolicy policy = new("[nudge] incomplete wiring");
    SendMessageCommandHandler handler = new(agent, conversation, policy);

    _ = await handler.Handle(new SendMessageCommand("hello"), ct: TestContext.Current.CancellationToken);

    Assert.Empty(policy.ContextsSeen);
    Assert.Equal(2, conversation.Messages.Count);
  }

  [Fact]
  public async Task Handle_LegacySingleArgConstruction_WorksAndNeverNudges()
  {
    ScriptedProvider provider = new(Result.Success(new ModelResponse("ok", [])));
    Ag agent = new(provider, new Conversation(),
        ModelConfig.Create("m", null, 100, 0.5f, 8192).Value!, new ToolRegistry([]));
    SendMessageCommandHandler handler = new(agent);

    Result<string> result = await handler.Handle(new SendMessageCommand("hello"), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal([Role.User, Role.Assistant],
        agent.Conversation.Messages.Select(m => m.Role));
  }

  private static (Ag Agent, Conversation Conversation) BuildAgent(IModelProvider provider)
  {
    Conversation conversation = new();
    Ag agent = new(provider, conversation,
        ModelConfig.Create("m", null, 100, 0.5f, 8192).Value!, new ToolRegistry([]));
    return (agent, conversation);
  }

  /// <summary>In-proc scripted provider: replays queued results, then a final plain success.</summary>
  private sealed class ScriptedProvider(params Result<ModelResponse>[] responses) : IModelProvider
  {
    private readonly Queue<Result<ModelResponse>> _queue = new(responses);

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
        => Task.FromResult(_queue.Count > 0 ? _queue.Dequeue()
            : Result.Success(new ModelResponse("fin", [])));
  }

  /// <summary>Records every evaluated context; returns its fixed line (null = silent).</summary>
  private sealed class CountingPolicy(string? line) : INudgePolicy
  {
    public List<NudgeContext> ContextsSeen { get; } = [];

    public string? Evaluate(NudgeContext context)
    {
      ContextsSeen.Add(context);
      return line;
    }
  }

  private sealed class StubTool(string name, string resultContent) : ITool
  {
    public ToolDefinition Definition { get; } = new ToolDefinition(name, "desc", []);

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
        => Task.FromResult(new ToolResult(resultContent, false));
  }
}
