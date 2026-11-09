using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>The over-provisioned nudge: a run that finishes inside a tenth of the budget
///     it asked for earns one System line naming both numbers and the narrowing move.
///     It speaks at most once per turn, never repeats on the next iteration, and speaks
///     again in the next turn — the fact is worth stating once, not on every cheap run.</summary>
public class AgentBudgetNudgeTests
{
  private static readonly ModelConfig Config = ModelConfig.Create("test-model", 100, 0.5f, 1000).Value!;

  /// <summary>One iteration per queued response; a tool-call response forces another
  ///     iteration against the same turn.</summary>
  private sealed class ScriptedProvider(params ModelResponse[] responses) : IModelProvider
  {
    private readonly Queue<ModelResponse> _responses = new(responses);

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
        => Task.FromResult(Result.Success(_responses.Dequeue()));

    public Task<Result<ModelResponse>> SendStreamingAsync(ModelConfig config, ModelRequest request,
        Action<string>? onContentDelta = null, Action<string>? onReasoningDelta = null, CancellationToken ct = default)
        => SendAsync(config, request, ct);
  }

  /// <summary>A tool whose result carries a cost the test states.</summary>
  private sealed class CostedTool(ExecCost? cost) : ITool
  {
    public ToolDefinition Definition { get; } = new("run", "runs",
        [new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber,
            ToolTimeout.ParameterDescription, Minimum: 1)]);

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
        => Task.FromResult(new ToolResult("ok", false, Cost: cost));
  }

  private static Agent NewAgent(IModelProvider provider, ITool tool)
      => new(provider, new Conversation(), Config, new ToolRegistry([tool]), new AgentOptions());

  private static ModelResponse ToolCall(string id)
      => new(null, [new ToolCallRequest(id, "run", /*lang=json,strict*/ "{\"timeoutSeconds\":600}")]);

  private static int NudgeCount(Conversation conversation)
      => conversation.Messages.Count(m => m.Role is Role.System
          && m.Content.StartsWith(Agent.OverProvisionedPrefix, StringComparison.Ordinal));

  [Fact]
  public async Task OverProvisioned_Run_Speaks_Once_And_Names_Both_Numbers()
  {
    Agent agent = NewAgent(
        new ScriptedProvider(ToolCall("c1"), new ModelResponse("done", [])),
        new CostedTool(new ExecCost(TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(600))));

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Message nudge = Assert.Single(agent.Conversation.Messages,
        m => m.Role is Role.System && m.Content.StartsWith(Agent.OverProvisionedPrefix, StringComparison.Ordinal));
    Assert.Contains("1.2s", nudge.Content, StringComparison.Ordinal);
    Assert.Contains("600s", nudge.Content, StringComparison.Ordinal);
    Assert.Contains("narrowest", nudge.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("larger", nudge.Content, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task Run_At_Or_Above_A_Tenth_Of_Its_Budget_Stays_Quiet()
  {
    Agent agent = NewAgent(
        new ScriptedProvider(ToolCall("c1"), new ModelResponse("done", [])),
        new CostedTool(new ExecCost(TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(600))));

    _ = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.Equal(0, NudgeCount(agent.Conversation));
  }

  [Fact]
  public async Task Run_With_No_Cost_Stays_Quiet()
  {
    Agent agent = NewAgent(
        new ScriptedProvider(ToolCall("c1"), new ModelResponse("done", [])),
        new CostedTool(null));

    _ = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.Equal(0, NudgeCount(agent.Conversation));
  }

  [Fact]
  public async Task Two_OverProvisioned_Runs_In_One_Turn_Speak_Once()
  {
    Agent agent = NewAgent(
        new ScriptedProvider(ToolCall("c1"), ToolCall("c2"), new ModelResponse("done", [])),
        new CostedTool(new ExecCost(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(600))));

    _ = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.Equal(1, NudgeCount(agent.Conversation));
  }

  [Fact]
  public async Task The_Next_Turn_May_Speak_Again()
  {
    Agent agent = NewAgent(
        new ScriptedProvider(
            ToolCall("c1"), new ModelResponse("done", []),
            ToolCall("c2"), new ModelResponse("done", [])),
        new CostedTool(new ExecCost(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(600))));

    _ = await agent.SendMessage("first", ct: TestContext.Current.CancellationToken);
    Assert.Equal(1, NudgeCount(agent.Conversation));

    _ = await agent.SendMessage("second", ct: TestContext.Current.CancellationToken);
    Assert.Equal(2, NudgeCount(agent.Conversation));
  }
}
