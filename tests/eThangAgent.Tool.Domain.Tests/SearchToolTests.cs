using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

public class SearchToolTests
{
  private const string Root = @"C:\ws";

  private static SearchTool MakeTool(Result<FileSearch> outcome, out CapturingFake fake)
  {
    fake = new CapturingFake(outcome);
    return new(new WorkspacePathResolver(Root), fake);
  }

  // Every search payload is a JSON object; inject the mandatory budget here so the
  // individual tests can stay focused on their own parameter under test.
  private static Task<ToolResult> Run(SearchTool tool, string json) =>
      tool.ExecuteAsync(new RawToolInput("search_files",
          json.StartsWith('{') ? "{\"timeoutSeconds\":120," + json[1..] : json));

  // ---- Missing parameters ----

  [Fact]
  public async Task MissingPattern_ReturnsError()
  {
    ToolResult r = await Run(MakeTool(null!, out _),
                                 /*lang=json,strict*/
                                 """{"mode":"Literal","maxResults":50}""");
    Assert.True(r.IsError);
    Assert.Contains("pattern", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MissingMode_ReturnsError()
  {
    ToolResult r = await Run(MakeTool(null!, out _),
                                 /*lang=json,strict*/
                                 """{"pattern":"x","maxResults":50}""");
    Assert.True(r.IsError);
    Assert.Contains("mode", r.Content, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("literal")]
  [InlineData("bogus")]
  public async Task ModeMustBeExactEnum(string mode)
  {
    ToolResult r = await Run(MakeTool(null!, out _),
            "{\"pattern\":\"x\",\"mode\":\"" + mode + "\",\"maxResults\":50}");
    Assert.True(r.IsError);
    Assert.Contains("Literal", r.Content, StringComparison.Ordinal);
    Assert.Contains("Regex", r.Content, StringComparison.Ordinal);
  }

  // ---- maxResults rules ----

  [Fact]
  public async Task MissingMaxResults_ReturnsError()
  {
    ToolResult r = await Run(MakeTool(null!, out _),
                                 /*lang=json,strict*/
                                 """{"pattern":"x","mode":"Literal"}""");
    Assert.True(r.IsError);
    Assert.Contains("maxResults", r.Content, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-5)]
  public async Task NonPositiveMaxResults_ReturnsError(int max)
  {
    ToolResult r = await Run(MakeTool(null!, out _),
            "{\"pattern\":\"x\",\"mode\":\"Literal\",\"maxResults\":" + max + "}");
    Assert.True(r.IsError);
    Assert.Contains("maxResults", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task OvershootMaxResults_ClampedWithVisibleWarning()
  {
    Result<FileSearch> outcome = Result.Success(new FileSearch(
        [new SearchMatch(@"C:\ws\a.cs", 1, ["hit"])], true, 3));
    ToolResult r = await Run(MakeTool(outcome, out CapturingFake? fake),
                                 /*lang=json,strict*/
                                 """{"pattern":"hit","mode":"Literal","maxResults":500,"path":"."}""");
    Assert.False(r.IsError);
    Assert.Equal(200, fake.MaxResults!);          // clamp reached the backend
    Assert.Contains("results capped at 200", r.Content, StringComparison.Ordinal); // visible, not silent
  }

  // ---- contextLines ----

  [Fact]
  public async Task NegativeContextLines_ReturnsError()
  {
    ToolResult r = await Run(MakeTool(null!, out _),
                                 /*lang=json,strict*/
                                 """{"pattern":"x","mode":"Literal","maxResults":50,"contextLines":-1}""");
    Assert.True(r.IsError);
    Assert.Contains("contextLines", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AbsentContextLines_DefaultsToZero()
  {
    Result<FileSearch> outcome = Result.Success(new FileSearch([], false, 1));
    _ = await Run(MakeTool(outcome, out CapturingFake? fake),
                             /*lang=json,strict*/
                             """{"pattern":"x","mode":"Literal","maxResults":50,"path":"."}""");
    Assert.Equal(0, fake.ContextLines);
  }

  // ---- Types & unknown ----

  [Fact]
  public async Task UnknownParameter_Rejected()
  {
    ToolResult r = await Run(MakeTool(null!, out _),
                                 /*lang=json,strict*/
                                 """{"pattern":"x","mode":"Literal","maxResults":50,"ignoreCase":true}""");
    Assert.True(r.IsError);
    Assert.Contains("Unknown parameter", r.Content, StringComparison.Ordinal);
    Assert.Contains("ignoreCase", r.Content, StringComparison.Ordinal);
  }

  // ---- Path jail ----

  [Fact]
  public async Task PathOutsideWorkspace_ReturnsResolverError()
  {
    ToolResult r = await Run(MakeTool(null!, out _),
                                 /*lang=json,strict*/
                                 """{"pattern":"x","mode":"Literal","maxResults":50,"path":"..\\evil"}""");
    Assert.True(r.IsError);
    Assert.Contains("PathOutsideWorkspace", r.Content, StringComparison.Ordinal);
  }

  // ---- Success formatting ----

  [Fact]
  public async Task Matches_FormatHeaderFilesAndGutters()
  {
    Result<FileSearch> outcome = Result.Success(new FileSearch(
    [
        new SearchMatch(@"C:\ws\src\a.cs", 2, ["alpha", "beta", "gamma"]),
            new SearchMatch(@"C:\ws\b.txt", 2, ["top", "hit", "end"]),
        ], false, 2));
    ToolResult r = await Run(MakeTool(outcome, out _),
                                 /*lang=json,strict*/
                                 """{"pattern":"hit","mode":"Literal","maxResults":50,"path":".","contextLines":1}""");
    Assert.False(r.IsError);
    string expected =
        "[search 'hit' under C:\\ws: 2 match(es) across 2 file(s), 2 files scanned]\n" +
        "--- src\\a.cs ---\n" +
        "1\u2192 alpha\n2\u2192 beta\n3\u2192 gamma\n" +
        "--- b.txt ---\n" +
        "1\u2192 top\n2\u2192 hit\n3\u2192 end";
    Assert.Equal(expected, r.Content);
  }

  [Fact]
  public async Task Truncated_AppendsVisibleWarning()
  {
    Result<FileSearch> outcome = Result.Success(new FileSearch(
        [new SearchMatch(@"C:\ws\a.cs", 1, ["hit"])], true, 9));
    ToolResult r = await Run(MakeTool(outcome, out _),
                                 /*lang=json,strict*/
                                 """{"pattern":"hit","mode":"Literal","maxResults":50,"path":"."}""");
    Assert.False(r.IsError);
    Assert.EndsWith("[warning] results capped at 50 matches; narrow with pattern/path/glob to see more", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task NoMatches_CollapsesToSingleLine()
  {
    Result<FileSearch> outcome = Result.Success(new FileSearch([], false, 7));
    ToolResult r = await Run(MakeTool(outcome, out _),
                                 /*lang=json,strict*/
                                 """{"pattern":"zzz","mode":"Literal","maxResults":50,"path":"."}""");
    Assert.False(r.IsError);
    Assert.Equal($"[search 'zzz' under {Root}: no matches (7 files scanned)]", r.Content);
  }

  [Fact]
  public async Task BackendInvalidPattern_SurfacesVerbatim()
  {
    Result<FileSearch> outcome = Result.Failure<FileSearch>(
        new DomainError("InvalidPattern", "Invalid regular expression 'foo(': bad capture"));
    ToolResult r = await Run(MakeTool(outcome, out _),
                                 /*lang=json,strict*/
                                 """{"pattern":"foo(","mode":"Regex","maxResults":50,"path":"."}""");
    Assert.True(r.IsError);
    Assert.Contains("Error [InvalidPattern]", r.Content, StringComparison.Ordinal);
  }

  private sealed class CapturingFake(Result<FileSearch> outcome) : ISearchAccess
  {
    public object? MaxResults { get; private set; }
    public int ContextLines { get; private set; } = -1;

    public Task<Result<FileSearch>> SearchFilesAsync(
        string rootPath, string pattern, bool regex, string? glob,
        int maxResults, int contextLines, CancellationToken ct = default)
    {
      MaxResults = maxResults;
      ContextLines = contextLines;
      return Task.FromResult(outcome);
    }
  }
}
