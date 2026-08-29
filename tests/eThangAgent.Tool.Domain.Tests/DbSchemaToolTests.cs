using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

public class DbSchemaToolTests
{
  private const string Args = /*lang=json,strict*/ """{"timeoutSeconds":120}""";

  private static DbSchemaTool MakeTool(Result<SelfDatabaseSchema> outcome)
  {
    FakeSelfDatabaseAccess access = new() { SchemaOutcome = outcome };
    return new DbSchemaTool(access);
  }

  private static SelfDatabaseSchema SampleSchema(bool withCounts) => new(
      SchemaVersion: 8,
      Objects:
      [
          new SchemaObject(
              "state_keys",
              IsView: false,
              withCounts ? 41 : null,
              [
                  new SchemaColumn("workspace_id", "TEXT", NotNull: true, IsPrimaryKey: true, DefaultValue: null),
                  new SchemaColumn("value", "TEXT", NotNull: true, IsPrimaryKey: false, DefaultValue: "'x'"),
              ],
              [
                  new SchemaIndex("ix_one", IsUnique: false, ["value"]),
                  new SchemaIndex("sqlite_autoindex_state_keys_1", IsUnique: true, ["workspace_id"]),
              ]),
          new SchemaObject("my_view", IsView: true, withCounts ? 0 : null, [], []),
      ]);

  // ---- Parameter validation ----

  [Fact]
  public async Task UnknownParameter_Rejected()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("db_schema",
                                 /*lang=json,strict*/ """{"timeoutSeconds":120,"counts":true}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Unknown parameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("counts", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task IncludeCountsAsString_Rejected()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("db_schema",
                                 /*lang=json,strict*/ """{"timeoutSeconds":120,"includeCounts":"yes"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("boolean", result.Content, StringComparison.Ordinal);
  }

  // ---- Flag propagation ----

  [Fact]
  public async Task IncludeCountsOmitted_PassesFalse()
  {
    FakeSelfDatabaseAccess access = new() { SchemaOutcome = Result.Success(SampleSchema(false)) };
    _ = await new DbSchemaTool(access).ExecuteAsync(new RawToolInput("db_schema", Args), ct: TestContext.Current.CancellationToken);
    Assert.False(access.DescribeIncludeCounts);
  }

  [Fact]
  public async Task IncludeCountsTrue_PassedThrough()
  {
    FakeSelfDatabaseAccess access = new() { SchemaOutcome = Result.Success(SampleSchema(true)) };
    _ = await new DbSchemaTool(access).ExecuteAsync(new RawToolInput("db_schema",
        /*lang=json,strict*/ """{"timeoutSeconds":120,"includeCounts":true}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(access.DescribeIncludeCounts);
  }

  // ---- Success formatting ----

  [Fact]
  public async Task Success_RendersHeaderObjectsColumnsAndIndexes()
  {
    ToolResult result = await MakeTool(Result.Success(SampleSchema(withCounts: false)))
        .ExecuteAsync(new RawToolInput("db_schema", Args), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal(
        string.Join(Environment.NewLine,
            "[db_schema] schema version 8, 1 table(s), 2 index(es), 1 view(s)",
            "table state_keys",
            "  workspace_id TEXT PK NOT NULL",
            "  value TEXT NOT NULL DEFAULT 'x'",
            "  index ix_one (value)",
            "  unique index sqlite_autoindex_state_keys_1 (workspace_id)",
                        "view my_view"),
        result.Content);
  }

  [Fact]
  public async Task IncludeCounts_AppendsRowCounts()
  {
    ToolResult result = await MakeTool(Result.Success(SampleSchema(withCounts: true)))
        .ExecuteAsync(new RawToolInput("db_schema",
            /*lang=json,strict*/ """{"timeoutSeconds":120,"includeCounts":true}"""), ct: TestContext.Current.CancellationToken);
    Assert.Contains("table state_keys (41 rows)", result.Content, StringComparison.Ordinal);
    Assert.Contains("view my_view (0 rows)", result.Content, StringComparison.Ordinal);
  }

  // ---- Backend errors surface verbatim ----

  [Fact]
  public async Task BackendErrors_SurfaceVerbatim()
  {
    ToolResult result = await MakeTool(Result.Failure<SelfDatabaseSchema>(
            new DomainError("QueryFailed", "SQLite Error 14: 'unable to open database file'.")))
        .ExecuteAsync(new RawToolInput("db_schema", Args), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [QueryFailed]", result.Content, StringComparison.Ordinal);
    Assert.Contains("unable to open database file", result.Content, StringComparison.Ordinal);
  }
}
