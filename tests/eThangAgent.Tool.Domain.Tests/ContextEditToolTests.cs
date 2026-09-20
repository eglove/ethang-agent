using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

public class ContextEditToolTests
{
  private sealed class FakeContextService : IConversationContextService
  {
    public string Listed { get; set; } = "[1] [User] hi";
    public Result<string> RemoveResult { get; set; } = Result.Success("[context: shrank 2 message(s): removed last 2.]");
    public Result<string> ShortenResult { get; set; } = Result.Success("[context: shrank 1 message(s): message 1 shortened.]");

    public string List() => Listed;

    public Result<string> Remove(string selection) => RemoveResult;

    public Result<string> Shorten(string selection, string text) => ShortenResult;
  }

  private static RawToolInput Input(string json) => new("context_edit", json);

  [Fact]
  public void Definition_ExposesThreeActions_RequiresOnlyTimeout()
  {
    ContextEditTool tool = new(new FakeContextService());
    Assert.Equal("context_edit", tool.Definition.Name);
    Assert.Equal(["timeoutSeconds", "action"], tool.Definition.RequiredParameters);
    Assert.Equal(4, tool.Definition.Parameters.Count);
  }

  [Fact]
  public async Task List_ReturnsVerbatimRender()
  {
    FakeContextService service = new();
    ContextEditTool tool = new(service);
    ToolResult result = await tool.ExecuteAsync(Input(/*lang=json,strict*/"""{"timeoutSeconds":5, "action": "List" }"""), TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal("[1] [User] hi", result.Content);
  }

  [Fact]
  public async Task Remove_PassesSelection_ReturnsSentinelVerbatim()
  {
    FakeContextService service = new();
    ContextEditTool tool = new(service);
    ToolResult result = await tool.ExecuteAsync(Input(/*lang=json,strict*/"""{"timeoutSeconds":5, "action": "Remove", "selection": "last 2" }"""), TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Contains("[context: shrank 2 message(s)", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Shorten_PassesSelectionAndText()
  {
    FakeContextService service = new();
    ContextEditTool tool = new(service);
    ToolResult result = await tool.ExecuteAsync(Input(/*lang=json,strict*/"""{"timeoutSeconds":5, "action": "Shorten", "selection": "4", "text": "condensed" }"""), TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal("[context: shrank 1 message(s): message 1 shortened.]", result.Content);
  }

  [Fact]
  public async Task ServiceFailure_SurfacesAsErrorResult_Verbatim()
  {
    FakeContextService service = new()
    {
      RemoveResult = Result.Failure<string>(new DomainError("UnansweredToolCall", "Assistant tool call c1 at position 2 has no later tool result.")),
    };
    ContextEditTool tool = new(service);
    ToolResult result = await tool.ExecuteAsync(Input(/*lang=json,strict*/"""{"timeoutSeconds":5, "action": "Remove", "selection": "3" }"""), TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Equal("Error [UnansweredToolCall]: Assistant tool call c1 at position 2 has no later tool result.", result.Content);
  }

  [Fact]
  public async Task MissingAction_Fails_MissingParameter()
  {
    ContextEditTool tool = new(new FakeContextService());
    ToolResult result = await tool.ExecuteAsync(Input(/*lang=json,strict*/"""{"timeoutSeconds":5}"""), TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.StartsWith("Error [MissingParameter]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UnknownAction_Fails_InvalidParameterValue()
  {
    ContextEditTool tool = new(new FakeContextService());
    ToolResult result = await tool.ExecuteAsync(Input(/*lang=json,strict*/"""{"timeoutSeconds":5, "action": "Nuke" }"""), TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidParameterValue]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RemoveWithoutSelection_Fails_MissingParameter()
  {
    ContextEditTool tool = new(new FakeContextService());
    ToolResult result = await tool.ExecuteAsync(Input(/*lang=json,strict*/"""{"timeoutSeconds":5, "action": "Remove" }"""), TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.StartsWith("Error [MissingParameter]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ShortenWithoutText_Fails_MissingParameter()
  {
    ContextEditTool tool = new(new FakeContextService());
    ToolResult result = await tool.ExecuteAsync(Input(/*lang=json,strict*/"""{"timeoutSeconds":5, "action": "Shorten", "selection": "1" }"""), TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.StartsWith("Error [MissingParameter]", result.Content, StringComparison.Ordinal);
  }
}
