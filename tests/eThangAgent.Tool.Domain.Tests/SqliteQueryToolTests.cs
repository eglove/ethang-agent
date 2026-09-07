using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>sqlite_query (grand plan): read-only SQL over ARBITRARY SQLite files
///     inside the workspace — db_query covers only the agent's own app database.
///     Same lexical gate, same annotation-line output contract, same strict input
///     rules; the path must resolve inside the workspace; the read-only connection
///     behind the seam is the enforcement backstop. Unit tests run on a fake seam.</summary>
public class SqliteQueryToolTests
{
  private const string Args = /*lang=json,strict*/ """{"timeoutSeconds":120,"path":"data/warehouse.db","sql":"SELECT 1"}""";

  private static SqliteQueryTool MakeTool(Result<SelfQueryResult>? outcome = null)
  {
    FakeSqliteFileAccess access = new()
    {
      QueryOutcome = outcome ?? Result.Success(new SelfQueryResult(
          ["n"], [[new SelfQueryCell("1", null)]], Truncated: false)),
    };
    return new SqliteQueryTool(new WorkspacePathResolver(TestRoot), access);
  }

  private const string TestRoot = @"C:\tmp\ws";

  // ---- Parameter validation ----

  [Fact]
  public async Task MissingPath_ReturnsError()
  {
    ToolResult result = await MakeTool().ExecuteAsync(new RawToolInput("sqlite_query",
        /*lang=json,strict*/ """{"timeoutSeconds":120,"sql":"SELECT 1"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("path", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MissingSql_ReturnsError()
  {
    ToolResult result = await MakeTool().ExecuteAsync(new RawToolInput("sqlite_query",
        /*lang=json,strict*/ """{"timeoutSeconds":120,"path":"a.db"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("sql", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UnknownParameter_Rejected()
  {
    ToolResult result = await MakeTool().ExecuteAsync(new RawToolInput("sqlite_query",
        /*lang=json,strict*/ """{"timeoutSeconds":120,"path":"a.db","sql":"SELECT 1","limit":5}"""), ct: TestContext.Current.CancellationToken);
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
    ToolResult result = await MakeTool().ExecuteAsync(new RawToolInput("sqlite_query",
        /*lang=json,strict*/
        $$"""{"timeoutSeconds":120,"path":"a.db","sql":"SELECT 1","maxRows":{{rows}}}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("maxRows", result.Content, StringComparison.Ordinal);
    Assert.Contains("1000", result.Content, StringComparison.Ordinal);
  }

  // ---- Path and SQL guards run before the access ----

  [Fact]
  public async Task PathOutsideWorkspace_Rejected_AccessNeverCalled()
  {
    FakeSqliteFileAccess access = new();
    SqliteQueryTool tool = new(new WorkspacePathResolver(TestRoot), access);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("sqlite_query",
        /*lang=json,strict*/ """{"timeoutSeconds":120,"path":"C:\\elsewhere\\a.db","sql":"SELECT 1"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [PathOutsideWorkspace]", result.Content, StringComparison.Ordinal);
    Assert.Null(access.QueriedPath);
  }

  [Fact]
  public async Task WriteSql_Rejected_AccessNeverCalled()
  {
    FakeSqliteFileAccess access = new();
    SqliteQueryTool tool = new(new WorkspacePathResolver(TestRoot), access);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("sqlite_query",
        /*lang=json,strict*/ """{"timeoutSeconds":120,"path":"a.db","sql":"DROP TABLE agents"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [InvalidSql]", result.Content, StringComparison.Ordinal);
    Assert.Null(access.QueriedPath);
  }

  [Fact]
  public async Task MaxRowsOmitted_PassesTheDefault()
  {
    FakeSqliteFileAccess access = new()
    {
      QueryOutcome = Result.Success(new SelfQueryResult(["n"], [], Truncated: false)),
    };
    SqliteQueryTool tool = new(new WorkspacePathResolver(TestRoot), access);
    _ = await tool.ExecuteAsync(new RawToolInput("sqlite_query", Args), ct: TestContext.Current.CancellationToken);
    Assert.Equal("SELECT 1", access.QueriedSql);
    Assert.Equal(DbQueryToolInput.DefaultMaxRows, access.QueriedMaxRows);
  }

  // ---- Success formatting: same contract as db_query, [sqlite_query] tag ----

  [Fact]
  public async Task Success_RendersAnnotationHeaderAndPipeTable()
  {
    SelfQueryResult outcome = new(
        ["name", "type"],
        [
            [new SelfQueryCell("items", null), new SelfQueryCell("table", null)],
            [new SelfQueryCell("orders", null), new SelfQueryCell("table", null)],
        ],
        Truncated: false);
    ToolResult result = await MakeTool(Result.Success(outcome)).ExecuteAsync(
        new RawToolInput("sqlite_query", Args), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal(
        string.Join(Environment.NewLine,
            "[sqlite_query C:\\tmp\\ws\\data\\warehouse.db] 2 row(s) shown, 2 column(s)",
            "name | type",
            "-----+-----",
            "items | table",
            "orders | table"),
        result.Content);
  }

  [Fact]
  public async Task Truncated_AnnotationCarriesTheMarker()
  {
    SelfQueryResult outcome = new(["c"], [[new SelfQueryCell("x", null)]], Truncated: true);
    ToolResult result = await MakeTool(Result.Success(outcome)).ExecuteAsync(
        new RawToolInput("sqlite_query", Args), ct: TestContext.Current.CancellationToken);
    Assert.StartsWith("[sqlite_query ", result.Content, StringComparison.Ordinal);
    Assert.Contains(
        "1 row(s) shown, 1 column(s); result set truncated — add or raise LIMIT",
        result.Content, StringComparison.Ordinal);
  }

  // ---- Backend errors surface verbatim (not a database, locked, missing file) ----

  [Fact]
  public async Task BackendErrors_SurfaceVerbatim()
  {
    ToolResult result = await MakeTool(Result.Failure<SelfQueryResult>(
            new DomainError("QueryFailed", "SQLite Error 26: 'file is not a database'.")))
        .ExecuteAsync(new RawToolInput("sqlite_query", Args), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [QueryFailed]", result.Content, StringComparison.Ordinal);
    Assert.Contains("file is not a database", result.Content, StringComparison.Ordinal);
  }

  // ---- Advertised contract ----

  [Fact]
  public void Description_PinsTheOutputContract_AndRequiredParams()
  {
    string description = new SqliteQueryTool(
        new WorkspacePathResolver(TestRoot), new FakeSqliteFileAccess()).Definition.Description;
    Assert.Contains("[sqlite_query resolved-path]", description, StringComparison.Ordinal);
    Assert.Contains("read-only", description, StringComparison.Ordinal);
    Assert.Contains("inside the workspace", description, StringComparison.Ordinal);
  }

  [Fact]
  public void RequiredParameters_AreTimeoutPathAndSql()
  {
    ToolDefinition def = new SqliteQueryTool(
        new WorkspacePathResolver(TestRoot), new FakeSqliteFileAccess()).Definition;
    Assert.Equal(["timeoutSeconds", "path", "sql"], def.RequiredParameters);
  }
}
