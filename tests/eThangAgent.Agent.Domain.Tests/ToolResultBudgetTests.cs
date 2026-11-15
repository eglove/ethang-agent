using System.Text;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

public class ToolResultBudgetTests
{
  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", 100, 0.5f, 8192).Value!;

  /// <summary>A two-byte UTF-8 character: the measurement must count encoded bytes, not chars.</summary>
  private const char TwoByteChar = '\u00E9';

  [Fact]
  public void Apply_AtTheLimit_ReturnsTheSameInstance()
  {
    ToolResult result = new(new string('a', ToolResultBudget.MaxBytes), false, "read");

    ToolResult applied = ToolResultBudget.Apply(result);

    Assert.Same(result, applied);
    Assert.Equal(ToolResultBudget.MaxBytes, Encoding.UTF8.GetByteCount(applied.Content));
  }

  [Fact]
  public void Apply_OneByteOverTheLimit_IsAnErrorResultNamingBothNumbers()
  {
    ToolResult applied = ToolResultBudget.Apply(
        new ToolResult(new string('a', ToolResultBudget.MaxBytes + 1), false, "read"));

    Assert.True(applied.IsError);
    Assert.StartsWith("Error [ToolResultTooLarge]:", applied.Content, StringComparison.Ordinal);
    Assert.Contains((ToolResultBudget.MaxBytes + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
        applied.Content, StringComparison.Ordinal);
    Assert.Contains(ToolResultBudget.MaxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        applied.Content, StringComparison.Ordinal);
    Assert.Contains("line range", applied.Content, StringComparison.Ordinal);
    Assert.Contains("LIMIT", applied.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Apply_TwoByteText_MeasuresEncodedBytes_NotCharacters()
  {
    ToolResult atLimit = new(new string(TwoByteChar, ToolResultBudget.MaxBytes / 2), false);
    ToolResult overLimit = new(new string(TwoByteChar, (ToolResultBudget.MaxBytes / 2) + 1), false);

    Assert.Equal(ToolResultBudget.MaxBytes, Encoding.UTF8.GetByteCount(atLimit.Content));
    Assert.True(Encoding.UTF8.GetByteCount(overLimit.Content) > ToolResultBudget.MaxBytes);
    Assert.True(overLimit.Content.Length <= ToolResultBudget.MaxBytes);

    Assert.Same(atLimit, ToolResultBudget.Apply(atLimit));
    Assert.True(ToolResultBudget.Apply(overLimit).IsError);
  }

  [Fact]
  public void Apply_ImagesAreNotMeasured()
  {
    string base64 = Convert.ToBase64String(new byte[5 * 1024 * 1024]);
    ToolResultImage image = new("image/png", base64);
    ToolResult result = new("ten bytes\n", false, "shot", [image]);

    ToolResult applied = ToolResultBudget.Apply(result);

    Assert.Same(result, applied);
    ToolResultImage kept = Assert.Single(applied.Images!);
    Assert.Equal(base64, kept.Base64Data);
  }

  [Fact]
  public void Apply_EmptyContent_IsUnchanged()
  {
    ToolResult empty = new(string.Empty, false);

    Assert.Same(empty, ToolResultBudget.Apply(empty));
  }

  [Fact]
  public async Task Turn_OverBudgetToolResult_RefusesIntoTheConversationAndTheObserver()
  {
    string oversized = new('a', ToolResultBudget.MaxBytes + 1);
    Agent agent = BuildAgent(oversized);
    List<(string Full, bool IsError, ToolResult? Rich)> observed = [];

    Result<string> result = await agent.SendMessage("go",
        callbacks: new TurnCallbacks(OnToolResult: (_, _, full, isError, rich) => observed.Add((full, isError, rich))),
        ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    string refusal = Assert.Single(agent.Conversation.Messages, m => m.Role is Role.Tool).Content;
    (string full, bool isError, ToolResult? rich) = Assert.Single(observed);
    Assert.StartsWith("Error [ToolResultTooLarge]:", refusal, StringComparison.Ordinal);
    Assert.Equal(refusal, full);
    Assert.True(isError);
    Assert.Equal(refusal, rich!.Content);
    Assert.DoesNotContain(oversized, refusal, StringComparison.Ordinal);
    Assert.Equal(1, CountToolMessages(agent));
  }

  [Fact]
  public async Task Turn_TwoByteTextOverTheLimit_IsRefused()
  {
    string oversized = new(TwoByteChar, (ToolResultBudget.MaxBytes / 2) + 1);
    Agent agent = BuildAgent(oversized);
    List<string> observed = [];

    Result<string> result = await agent.SendMessage("go",
        callbacks: new TurnCallbacks(OnToolResult: (_, _, full, _, _) => observed.Add(full)),
        ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    string refusal = Assert.Single(agent.Conversation.Messages, m => m.Role is Role.Tool).Content;
    Assert.StartsWith("Error [ToolResultTooLarge]:", refusal, StringComparison.Ordinal);
    Assert.Equal(refusal, Assert.Single(observed));
  }

  [Fact]
  public async Task Turn_WithinBudgetToolResult_ReachesBothConsumersUntouched()
  {
    string content = new('a', ToolResultBudget.MaxBytes);
    ToolResult original = new(content, false, "read");
    Agent agent = BuildAgent(original);
    List<(string Full, ToolResult? Rich)> observed = [];

    Result<string> result = await agent.SendMessage("go",
        callbacks: new TurnCallbacks(OnToolResult: (_, _, full, _, rich) => observed.Add((full, rich))),
        ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    string toolMessage = Assert.Single(agent.Conversation.Messages, m => m.Role is Role.Tool).Content;
    (string full, ToolResult? rich) = Assert.Single(observed);
    Assert.Same(content, toolMessage);
    Assert.Same(content, full);
    Assert.Same(original, rich);
  }

  private static int CountToolMessages(Agent agent)
      => agent.Conversation.Messages.Count(m => m.Role is Role.Tool);

  private static Agent BuildAgent(string toolContent)
      => BuildAgent(new ToolResult(toolContent, false, "read"));

  private static Agent BuildAgent(ToolResult toolResult)
  {
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("call_1", "read", "{}")])),
        Result.Success(new ModelResponse("done", [])));
    return new Agent(provider, new Conversation(), DefaultConfig, new ToolRegistry([new FixedTool(toolResult)]));
  }

  private sealed class FixedTool(ToolResult result) : ITool
  {
    public ToolDefinition Definition { get; } = new("read", "desc", []);

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
        => Task.FromResult(result);
  }
}
