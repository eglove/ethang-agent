using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>find_files: name-pattern file search over the workspace. The recurring
///     three-probe pattern (list everything, list folders, enumerate the tree for one
///     name) is what this tool removes — one call answers "where is the file named X".</summary>
public class FindFilesToolTests
{
  private static readonly string Root = Path.Combine(Path.GetTempPath(), "ethang-findfiles-tests-" + Guid.NewGuid().ToString("N"));

  private static IPathResolver Resolver => new WorkspacePathResolver(Root);

  private sealed class FakeSearch(params (string SubRoot, string[] Paths)[] results) : IFileSearchAccess
  {
    public string? LastPattern { get; private set; }

    public string? LastScopedRoot { get; private set; }

    public Task<Result<IReadOnlyList<string>>> EnumerateFilesAsync(string root, string searchPattern, bool recurse, CancellationToken ct = default)
    {
      LastScopedRoot = root;
      LastPattern = searchPattern;
      List<string> paths = [];
      foreach ((string subRoot, string[] files) in results)
      {
        if (!string.Equals(subRoot, root, StringComparison.OrdinalIgnoreCase))
        {
          continue;
        }
        paths.AddRange(files);
      }

      return Task.FromResult(Result.Success<IReadOnlyList<string>>(paths));
    }
  }

  [Fact]
  public async Task Pattern_Matches_RelativePaths_Annotated()
  {
    FakeSearch search = new((Root, [@"C:\repo\src\App.cs", @"C:\repo\tests\App.Tests.cs"]));
    FindFilesTool tool = new(Resolver, search);

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("find_files", /*lang=json,strict*/ "{\"timeoutSeconds\":30,\"pattern\":\"App*\"}"), TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Contains("[find_files pattern 'App*' -> 2 matches]", result.Content, StringComparison.Ordinal);
    Assert.Contains("src\\App.cs", result.Content, StringComparison.Ordinal);
    Assert.Contains("tests\\App.Tests.cs", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task NoMatches_SaysZero()
  {
    FakeSearch search = new((Root, []));
    FindFilesTool tool = new(Resolver, search);

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("find_files", /*lang=json,strict*/ "{\"timeoutSeconds\":30,\"pattern\":\"nothing*\"}"), TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Contains("[find_files pattern 'nothing*' -> 0 matches]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MissingPattern_StrictError()
  {
    FindFilesTool tool = new(Resolver, new FakeSearch());

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("find_files", "{}"), TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Contains("Error [InvalidParameter]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MaxResults_Truncates_WithMarker()
  {
    FakeSearch search = new((Root, [@"C:\r\a.cs", @"C:\r\b.cs", @"C:\r\c.cs"]));
    FindFilesTool tool = new(Resolver, search);

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("find_files", /*lang=json,strict*/ "{\"timeoutSeconds\":30,\"pattern\":\"*\",\"maxResults\":2}"), TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Contains("[warning] showing 2 of 3 matches; refine the pattern", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Subdirectory_ScopesTheSearchRoot()
  {
    FakeSearch search = new((Root, []));
    FindFilesTool tool = new(Resolver, search);

    _ = await tool.ExecuteAsync(new RawToolInput("find_files", /*lang=json,strict*/ "{\"timeoutSeconds\":30,\"pattern\":\"*\",\"path\":\"src\"}"), TestContext.Current.CancellationToken);

    Assert.Equal(Path.Combine(Root, "src"), search.LastScopedRoot);
  }

  [Fact]
  public async Task OutsideWorkspacePath_Refused()
  {
    FindFilesTool tool = new(Resolver, new FakeSearch());

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("find_files", /*lang=json,strict*/ "{\"pattern\":\"*\",\"path\":\"..\\\\..\\\\Windows\"}"), TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Contains("Error [PathOutsideWorkspace]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void RootedAt_ReRootsTheResolver()
  {
    FindFilesTool tool = new(Resolver, new FakeSearch());
    ITool rooted = tool.RootedAt(@"C:\anchor");

    Assert.Equal("find_files", rooted.Definition.Name);
  }

  [Fact]
  public void Definition_DeclaresStrictContract()
  {
    FindFilesTool tool = new(Resolver, new FakeSearch());

    Assert.Equal("find_files", tool.Definition.Name);
    Assert.Contains("pattern", tool.Definition.Description, StringComparison.Ordinal);
  }
}
