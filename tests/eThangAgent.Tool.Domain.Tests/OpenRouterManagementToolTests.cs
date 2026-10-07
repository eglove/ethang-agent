namespace eThangAgent.ToolDomain.Tests;

/// <summary>The 'openrouter_management' tool refocused on debugging (log review):
///     strict input parsing for the generation/credits/activity/analytics actions,
///     dispatch over the management seam, and the fixed output contract. The seam
///     is faked; the parser and renderer are the contract under test.</summary>
public class OpenRouterManagementToolTests
{
  private sealed class FakeManagementAccess(params OpenRouterManagementOutcome[] outcomes) : IOpenRouterManagementAccess
  {
    private int _next;
    public List<OpenRouterManagementCommand> Received { get; } = [];

    public Task<OpenRouterManagementOutcome> ExecuteAsync(OpenRouterManagementCommand command, CancellationToken ct = default)
    {
      Received.Add(command);
      return Task.FromResult(_next < outcomes.Length ? outcomes[_next++] : new OpenRouterManagementOutcome.Failure("UnexpectedCommand", "no canned outcome"));
    }
  }

  private static string Args(string inner) =>
      "{" + "\"timeoutSeconds\":120" + (inner.Length == 0 ? string.Empty : "," + inner) + "}";

  private static Task<ToolResult> Run(IOpenRouterManagementAccess access, string inner)
      => new OpenRouterManagementTool(access).ExecuteAsync(new RawToolInput("openrouter_management", Args(inner)),
          TestContext.Current.CancellationToken);

  // ── input parsing ───────────────────────────────────────────────────────

  [Fact]
  public async Task Missing_Action_Is_A_Typed_Error()
  {
    ToolResult r = await Run(new FakeManagementAccess(), string.Empty);
    Assert.StartsWith("Error [MissingParameter]", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Unknown_Action_Is_A_Typed_Error()
  {
    ToolResult r = await Run(new FakeManagementAccess(), "\"action\": \"rotate\"");
    Assert.StartsWith("Error [InvalidParameterValue]", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Generation_Requires_Id()
  {
    ToolResult r = await Run(new FakeManagementAccess(), "\"action\": \"generation\"");
    Assert.StartsWith("Error [MissingParameter]", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Generation_Id_Must_Match_The_Gen_Pattern()
  {
    ToolResult r = await Run(new FakeManagementAccess(), "\"action\": \"generation\", \"id\": \"abc123\"");
    Assert.StartsWith("Error [InvalidParameterValue]", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Generation_Id_Over_128_Characters_Is_Rejected()
  {
    string id = "gen-" + new string('a', 130);
    ToolResult r = await Run(new FakeManagementAccess(), $"\"action\": \"generation\", \"id\": \"{id}\"");
    Assert.StartsWith("Error [InvalidParameterValue]", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Unknown_Keys_Are_Rejected()
  {
    ToolResult r = await Run(new FakeManagementAccess(), "\"action\": \"credits\", \"bogus\": 1");
    Assert.StartsWith("Error [UnknownParameter]", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Activity_Date_Must_Be_YyyyMmDd()
  {
    ToolResult r = await Run(new FakeManagementAccess(), "\"action\": \"activity\", \"date\": \"08/24/2025\"");
    Assert.StartsWith("Error [InvalidParameterValue]", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Activity_Group_By_Must_Be_Workspace()
  {
    ToolResult r = await Run(new FakeManagementAccess(), "\"action\": \"activity\", \"group_by\": \"model\"");
    Assert.StartsWith("Error [InvalidParameterValue]", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Analytics_Query_Requires_Metrics()
  {
    ToolResult r = await Run(new FakeManagementAccess(), "\"action\": \"analytics_query\"");
    Assert.StartsWith("Error [MissingParameter]", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Analytics_Query_Dimensions_Cap_At_Two()
  {
    ToolResult r = await Run(new FakeManagementAccess(),
        "\"action\": \"analytics_query\", \"metrics\": [\"request_count\"], \"dimensions\": [\"model\", \"provider\", \"app\"]");
    Assert.StartsWith("Error [InvalidParameterValue]", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Analytics_Query_Time_Range_Must_Parse()
  {
    ToolResult r = await Run(new FakeManagementAccess(),
        "\"action\": \"analytics_query\", \"metrics\": [\"request_count\"], \"time_range_start\": \"not-a-date\"");
    Assert.StartsWith("Error [InvalidParameterValue]", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Analytics_Query_Limit_Must_Be_Positive()
  {
    ToolResult r = await Run(new FakeManagementAccess(),
        "\"action\": \"analytics_query\", \"metrics\": [\"request_count\"], \"limit\": 0");
    Assert.StartsWith("Error [InvalidParameterValue]", r.Content, StringComparison.Ordinal);
  }

  // ── dispatch ────────────────────────────────────────────────────────────

  [Fact]
  public async Task Generation_Carries_The_Id_And_Renders_The_Debug_Fields()
  {
    FakeManagementAccess access = new(new OpenRouterManagementOutcome.Generation(
        new OpenRouterGenerationRecord("gen-1", "m1", "p1", "r1", "2024-07-15T23:33:19Z", true, false,
            "stop", "stop", "priority", 10, 25, 5, 3, 0.0015, 0.0015, 1250, 1200, 50,
            false, "up-1", "req-1", 12345, "exa", "s1", 5)));
    ToolResult r = await Run(access, "\"action\": \"generation\", \"id\": \"gen-1\"");
    Assert.False(r.IsError);
    OpenRouterManagementCommand.GetGeneration cmd = Assert.IsType<OpenRouterManagementCommand.GetGeneration>(access.Received.Single());
    Assert.Equal("gen-1", cmd.Id);
    Assert.Contains("[openrouter-management] generation gen-1", r.Content, StringComparison.Ordinal);
    Assert.Contains("model: m1", r.Content, StringComparison.Ordinal);
    Assert.Contains("provider: p1", r.Content, StringComparison.Ordinal);
    Assert.Contains("finish_reason: stop", r.Content, StringComparison.Ordinal);
    Assert.Contains("prompt 10", r.Content, StringComparison.Ordinal);
    Assert.Contains("total 0.0015", r.Content, StringComparison.Ordinal);
    Assert.Contains("latency 1250 ms", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Activity_Carries_Date_And_GroupBy()
  {
    FakeManagementAccess access = new(new OpenRouterManagementOutcome.Activity([]));
    ToolResult r = await Run(access, "\"action\": \"activity\", \"date\": \"2025-08-24\", \"group_by\": \"workspace\"");
    Assert.False(r.IsError);
    OpenRouterManagementCommand.GetActivity cmd = Assert.IsType<OpenRouterManagementCommand.GetActivity>(access.Received.Single());
    Assert.Equal("2025-08-24", cmd.Date);
    Assert.Equal("workspace", cmd.GroupBy);
  }

  [Fact]
  public async Task Analytics_Query_Carries_Every_Field()
  {
    FakeManagementAccess access = new(new OpenRouterManagementOutcome.Analytics(
        new OpenRouterAnalyticsResult([], 0, false, null)));
    ToolResult r = await Run(access,
        "\"action\": \"analytics_query\", \"metrics\": [\"request_count\"], \"dimensions\": [\"model\"], " +
        "\"granularity\": \"day\", \"time_range_start\": \"2025-01-01T00:00:00Z\", \"time_range_end\": \"2025-01-08T00:00:00Z\", " +
        "\"group_limit\": 50, \"limit\": 100");
    Assert.False(r.IsError);
    OpenRouterManagementCommand.QueryAnalytics cmd = Assert.IsType<OpenRouterManagementCommand.QueryAnalytics>(access.Received.Single());
    Assert.Equal(["request_count"], cmd.Metrics);
    Assert.Equal(["model"], cmd.Dimensions);
    Assert.Equal("day", cmd.Granularity);
    Assert.Equal("2025-01-01T00:00:00Z", cmd.TimeRangeStart);
    Assert.Equal("2025-01-08T00:00:00Z", cmd.TimeRangeEnd);
    Assert.Equal(50, cmd.GroupLimit);
    Assert.Equal(100, cmd.Limit);
  }

  // ── rendering ───────────────────────────────────────────────────────────

  [Fact]
  public async Task Content_Renders_Error_Input_And_Output()
  {
    FakeManagementAccess access = new(new OpenRouterManagementOutcome.GenerationContent(
        new OpenRouterGenerationContent(null,
            /*lang=json,strict*/ "{\"messages\":[{\"content\":\"What is the meaning of life?\",\"role\":\"user\"}]}", "42", null)));
    ToolResult r = await Run(access, "\"action\": \"generation_content\", \"id\": \"gen-1\"");
    Assert.False(r.IsError);
    Assert.Contains("error: none", r.Content, StringComparison.Ordinal);
    Assert.Contains("What is the meaning of life?", r.Content, StringComparison.Ordinal);
    Assert.Contains("42", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Credits_Renders_Total_Used_And_Remaining()
  {
    FakeManagementAccess access = new(new OpenRouterManagementOutcome.Credits(new OpenRouterCreditsItem(100.5, 25.75)));
    ToolResult r = await Run(access, "\"action\": \"credits\"");
    Assert.False(r.IsError);
    Assert.Contains("100.5 total", r.Content, StringComparison.Ordinal);
    Assert.Contains("25.75 used", r.Content, StringComparison.Ordinal);
    Assert.Contains("74.75 remaining", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Activity_Renders_One_Line_Per_Row()
  {
    FakeManagementAccess access = new(new OpenRouterManagementOutcome.Activity(
    [
      new OpenRouterActivityRow("2025-08-24", "openai/gpt-4.1", "OpenAI", 5, 50, 125, 25, 10, 0.015, 0.012),
    ]));
    ToolResult r = await Run(access, "\"action\": \"activity\"");
    Assert.False(r.IsError);
    Assert.StartsWith("[openrouter-management] 1 activity row(s)", r.Content, StringComparison.Ordinal);
    Assert.Contains("openai/gpt-4.1", r.Content, StringComparison.Ordinal);
    Assert.Contains("5 req", r.Content, StringComparison.Ordinal);
    Assert.Contains("usage 0.015", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Analytics_Meta_Renders_Names_And_Labels()
  {
    FakeManagementAccess access = new(new OpenRouterManagementOutcome.AnalyticsMeta(
        [new OpenRouterAnalyticsField("request_count", "Request Count", "number")],
        [new OpenRouterAnalyticsField("model", "Model", null)],
        [new OpenRouterAnalyticsField("day", "Day", null)]));
    ToolResult r = await Run(access, "\"action\": \"analytics_meta\"");
    Assert.False(r.IsError);
    Assert.Contains("request_count (Request Count, number)", r.Content, StringComparison.Ordinal);
    Assert.Contains("model (Model)", r.Content, StringComparison.Ordinal);
    Assert.Contains("day (Day)", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Analytics_Renders_Rows_With_Metadata()
  {
    FakeManagementAccess access = new(new OpenRouterManagementOutcome.Analytics(
        new OpenRouterAnalyticsResult(
            [new Dictionary<string, string> { ["date__day"] = "2025-01-01T00:00:00.000Z", ["request_count"] = "1500" }],
            1, false, 42)));
    ToolResult r = await Run(access, "\"action\": \"analytics_query\", \"metrics\": [\"request_count\"]");
    Assert.False(r.IsError);
    Assert.Contains("1 row(s)", r.Content, StringComparison.Ordinal);
    Assert.Contains("truncated: false", r.Content, StringComparison.Ordinal);
    Assert.Contains("date__day=2025-01-01T00:00:00.000Z", r.Content, StringComparison.Ordinal);
    Assert.Contains("request_count=1500", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Failures_Render_Verbatim()
  {
    FakeManagementAccess access = new(new OpenRouterManagementOutcome.Failure("ManagementError",
        "OpenRouter management API returned HTTP 401: invalid key"));
    ToolResult r = await Run(access, "\"action\": \"credits\"");
    Assert.True(r.IsError);
    Assert.Equal("Error [ManagementError]: OpenRouter management API returned HTTP 401: invalid key", r.Content);
  }

  // ── advertisement honesty ───────────────────────────────────────────────

  [Fact]
  public void Only_TimeoutSeconds_And_Action_Are_Required()
  {
    ToolDefinition d = new OpenRouterManagementTool(new FakeManagementAccess()).Definition;
    Assert.Equal(["timeoutSeconds", "action"], d.RequiredParameters);
  }

  [Fact]
  public void Description_Documents_Actions_Credentials_And_Error_Codes()
  {
    string d = new OpenRouterManagementTool(new FakeManagementAccess()).Definition.Description;
    foreach (string a in new[] { "generation", "generation_content", "credits", "activity", "analytics_meta", "analytics_query" })
    {
      Assert.Contains(a, d, StringComparison.Ordinal);
    }

    Assert.Contains("management key", d, StringComparison.Ordinal);
    Assert.Contains("ManagementUnavailable", d, StringComparison.Ordinal);
    Assert.Contains("ManagementError", d, StringComparison.Ordinal);
    Assert.Contains("ManagementParseError", d, StringComparison.Ordinal);
  }
}
