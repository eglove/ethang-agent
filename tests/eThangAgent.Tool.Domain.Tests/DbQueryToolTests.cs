using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

public class DbQueryToolTests
{
  private const string Args = /*lang=json,strict*/ """{"timeoutSeconds":120,"sql":"SELECT 1"}""";

  private static DbQueryTool MakeTool(Result<SelfQueryResult> outcome)
  {
    FakeSelfDatabaseAccess access = new() { QueryOutcome = outcome };
    return new DbQueryTool(access);
  }

  // ---- Parameter validation ----

  [Fact]
  public async Task MissingSql_ReturnsError()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("db_query",
                                 /*lang=json,strict*/ """{"timeoutSeconds":120}"""));
    Assert.True(result.IsError);
    Assert.Contains("sql", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task EmptySql_ReturnsError()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("db_query",
                                 /*lang=json,strict*/ """{"timeoutSeconds":120,"sql":""}"""));
    Assert.True(result.IsError);
    Assert.Contains("non-empty", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UnknownParameter_Rejected()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("db_query",
                                 /*lang=json,strict*/ """{"timeoutSeconds":120,"sql":"SELECT 1","limit":5}"""));
    Assert.True(result.IsError);
    Assert.Contains("Unknown parameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("limit", result.Content, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-3)]
  [InlineData(1001)]
  public async Task MaxRowsOutsideBounds_Rejected(int rows)
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("db_query",
                                 /*lang=json,strict*/
                                 $$"""{"timeoutSeconds":120,"sql":"SELECT 1","maxRows":{{rows}}}"""));
    Assert.True(result.IsError);
    Assert.Contains("maxRows", result.Content, StringComparison.Ordinal);
    Assert.Contains("1000", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MaxRowsAsString_Rejected()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("db_query",
                                 /*lang=json,strict*/ """{"timeoutSeconds":120,"sql":"SELECT 1","maxRows":"5"}"""));
    Assert.True(result.IsError);
    Assert.Contains("integer", result.Content, StringComparison.Ordinal);
  }

  // ---- The lexical gate runs before the access ----

  [Fact]
  public async Task InvalidSql_Rejected_AccessNeverCalled()
  {
    FakeSelfDatabaseAccess access = new();
    DbQueryTool tool = new(access);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("db_query",
        /*lang=json,strict*/ """{"timeoutSeconds":120,"sql":"DROP TABLE agents"}"""));
    Assert.True(result.IsError);
    Assert.Contains("Error [InvalidSql]", result.Content, StringComparison.Ordinal);
    Assert.Null(access.QueriedSql);
  }

  [Fact]
  public async Task MaxRowsOmitted_PassesTheDefault()
  {
    FakeSelfDatabaseAccess access = new() { QueryOutcome = Result.Failure<SelfQueryResult>(new DomainError("Unused", "n/a")) };
    DbQueryTool tool = new(access);
    _ = await tool.ExecuteAsync(new RawToolInput("db_query", Args));
    Assert.Equal("SELECT 1", access.QueriedSql);
    Assert.Equal(DbQueryToolInput.DefaultMaxRows, access.QueriedMaxRows);
  }

  // ---- Success formatting ----

  [Fact]
  public async Task Success_RendersAnnotationHeaderAndPipeTable()
  {
    SelfQueryResult outcome = new(
        ["name", "type"],
        [
            [new SelfQueryCell("state_keys", null), new SelfQueryCell("table", null)],
            [new SelfQueryCell("agents", null), new SelfQueryCell("table", null)],
        ],
        Truncated: false);
    ToolResult result = await MakeTool(Result.Success(outcome)).ExecuteAsync(new RawToolInput("db_query", Args));
    Assert.False(result.IsError);
    Assert.Equal(
        string.Join(Environment.NewLine,
            "[db_query] 2 row(s) shown, 2 column(s)",
            "name | type",
            "-----+-----",
            "state_keys | table",
                        "agents | table"),
        result.Content);
  }

  [Fact]
  public async Task Truncated_AnnotationCarriesTheMarker()
  {
    SelfQueryResult outcome = new(["c"], [[new SelfQueryCell("x", null)]], Truncated: true);
    ToolResult result = await MakeTool(Result.Success(outcome)).ExecuteAsync(new RawToolInput("db_query", Args));
    Assert.False(result.IsError);
    Assert.StartsWith(
        "[db_query] 1 row(s) shown, 1 column(s); result set truncated — add or raise LIMIT",
        result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ZeroRows_StillShowsHeaderRowAndGutter()
  {
    SelfQueryResult outcome = new(["a", "b"], [], Truncated: false);
    ToolResult result = await MakeTool(Result.Success(outcome)).ExecuteAsync(new RawToolInput("db_query", Args));
    Assert.Equal(
        string.Join(Environment.NewLine,
            "[db_query] 0 row(s) shown, 2 column(s)",
            "a | b",
                        "-----+-----"),
        result.Content);
  }

  // ---- Cell rendering contract ----

  [Fact]
  public async Task Cells_RenderNullsBlobsAndEscapesVerbatim()
  {
    SelfQueryResult outcome = new(
        ["n", "b", "pipe", "nl", "tab", "slash"],
        [[
            SelfQueryCell.Null,
            new SelfQueryCell(null, 4),
            new SelfQueryCell("a|b", null),
            new SelfQueryCell("x\ny", null),
            new SelfQueryCell("x\ty", null),
            new SelfQueryCell("x\\y", null),
        ]],
        Truncated: false);
    ToolResult result = await MakeTool(Result.Success(outcome)).ExecuteAsync(new RawToolInput("db_query", Args));
    Assert.Contains(
        "<null> | <blob 4 bytes> | a\\|b | x\\ny | x\\ty | x\\\\y",
        result.Content, StringComparison.Ordinal);
  }

  // ---- Backend errors surface verbatim ----

  [Fact]
  public async Task BackendErrors_SurfaceVerbatim()
  {
    ToolResult result = await MakeTool(Result.Failure<SelfQueryResult>(
            new DomainError("QueryFailed", "SQLite Error 1: 'no such table: nope'.")))
        .ExecuteAsync(new RawToolInput("db_query", Args));
    Assert.True(result.IsError);
    Assert.Contains("Error [QueryFailed]", result.Content, StringComparison.Ordinal);
    Assert.Contains("no such table: nope", result.Content, StringComparison.Ordinal);
  }
}
