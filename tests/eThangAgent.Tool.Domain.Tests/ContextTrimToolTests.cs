using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

public class ContextTrimToolTests
{
  private sealed class FakeContextService : IConversationContextService
  {
    public string Listed { get; set; } = "[1] [User] hi";
    public Result<string> TrimResult { get; set; } = Result.Success("[context: shrank 2 message(s): trimmed last 2.]");
    public Result<string> ShortenResult { get; set; } = Result.Success("[context: shrank 1 message(s): message 1 shortened.]");
    public List<string> TrimCalls { get; } = [];
    public List<(string Selection, string Text)> ShortenCalls { get; } = [];

    public string List() => Listed;

    public Result<string> Remove(string selection) => Result.Success("[context: shrank]");

    public Result<string> Shorten(string selection, string text) => ShortenResult;

    public Result<string> TrimToolResults(string selection)
    {
      TrimCalls.Add(selection);
      return TrimResult;
    }

    public Result<string> ShortenToolResult(string selection, string text)
    {
      ShortenCalls.Add((selection, text));
      return ShortenResult;
    }
  }

  private static RawToolInput Input(string json) => new("context_trim", json);

  [Fact]
  public void Definition_ExposesTwoActions_RequiresOnlyTimeout()
  {
    ContextTrimTool tool = new(new FakeContextService());
    Assert.Equal("context_trim", tool.Definition.Name);
    Assert.Equal(["timeoutSeconds", "action"], tool.Definition.RequiredParameters);
    Assert.Equal(4, tool.Definition.Parameters.Count);
  }

  [Fact]
  public async Task Trim_PassesSelection_ReturnsSentinelVerbatim()
  {
    FakeContextService service = new();
    ContextTrimTool tool = new(service);
    ToolResult result = await tool.ExecuteAsync(Input(/*lang=json,strict*/"""{"timeoutSeconds":5, "action": "Trim", "selection": "last 2" }"""), TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal("[context: shrank 2 message(s): trimmed last 2.]", result.Content);
    Assert.Equal(["last 2"], service.TrimCalls);
  }

  [Fact]
  public async Task Shorten_PassesSelectionAndText()
  {
    FakeContextService service = new();
    ContextTrimTool tool = new(service);
    ToolResult result = await tool.ExecuteAsync(Input(/*lang=json,strict*/"""{"timeoutSeconds":5, "action": "Shorten", "selection": "4", "text": "kept the file path" }"""), TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal("[context: shrank 1 message(s): message 1 shortened.]", result.Content);
    Assert.Equal([("4", "kept the file path")], service.ShortenCalls);
  }

  [Fact]
  public async Task ServiceFailure_SurfacesAsErrorResult_Verbatim()
  {
    FakeContextService service = new()
    {
      TrimResult = Result.Failure<string>(new DomainError("ToolOnlySelection", "Selection 'last 2' names message 1, which is a User message; only tool results can be trimmed.")),
    };
    ContextTrimTool tool = new(service);
    ToolResult result = await tool.ExecuteAsync(Input(/*lang=json,strict*/"""{"timeoutSeconds":5, "action": "Trim", "selection": "last 2" }"""), TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Equal("Error [ToolOnlySelection]: Selection 'last 2' names message 1, which is a User message; only tool results can be trimmed.", result.Content);
  }

  [Fact]
  public async Task MissingAction_Fails_MissingParameter()
  {
    ContextTrimTool tool = new(new FakeContextService());
    ToolResult result = await tool.ExecuteAsync(Input(/*lang=json,strict*/"""{"timeoutSeconds":5}"""), TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.StartsWith("Error [MissingParameter]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UnknownAction_Fails_InvalidParameterValue()
  {
    ContextTrimTool tool = new(new FakeContextService());
    ToolResult result = await tool.ExecuteAsync(Input(/*lang=json,strict*/"""{"timeoutSeconds":5, "action": "Delete", "selection": "2" }"""), TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidParameterValue]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task TrimWithoutSelection_Fails_MissingParameter()
  {
    ContextTrimTool tool = new(new FakeContextService());
    ToolResult result = await tool.ExecuteAsync(Input(/*lang=json,strict*/"""{"timeoutSeconds":5, "action": "Trim" }"""), TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.StartsWith("Error [MissingParameter]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ShortenWithoutText_Fails_MissingParameter()
  {
    ContextTrimTool tool = new(new FakeContextService());
    ToolResult result = await tool.ExecuteAsync(Input(/*lang=json,strict*/"""{"timeoutSeconds":5, "action": "Shorten", "selection": "1" }"""), TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.StartsWith("Error [MissingParameter]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Definition_Carries_ToolOnly_And_Trim_Timing_Guidance()
  {
    ContextTrimTool tool = new(new FakeContextService());
    // The model must know only tool results are reachable, when to trim, and
    // what a good Shorten excerpt preserves.
    Assert.Contains("ONLY tool results", tool.Definition.Description, StringComparison.Ordinal);
    Assert.Contains("served its purpose", tool.Definition.Description, StringComparison.Ordinal);
    Assert.Contains("concrete identifiers", tool.Definition.Description, StringComparison.Ordinal);
  }
}
