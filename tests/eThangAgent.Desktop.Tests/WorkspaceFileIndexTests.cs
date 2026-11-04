using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.Tests;

public class WorkspaceFileIndexTests
{
  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  private sealed class FakeSearch(IReadOnlyList<string> paths) : IFileSearchAccess
  {
    public bool Fail { get; set; }

    public Task<Result<IReadOnlyList<string>>> EnumerateFilesAsync(
        string root, string searchPattern, bool recurse, CancellationToken ct = default)
      => Task.FromResult(Fail
          ? Result.Failure<IReadOnlyList<string>>(new DomainError("io", "boom"))
          : Result.Success(paths));
  }

  [Fact]
  public async Task FilenameMatchesRankAbovePathMatches()
  {
    WorkspaceFileIndex index = new(new FakeSearch([
      "C:/w/src/Agent/AgentSession.cs",
      "C:/w/src/AgentSession.cs",
    ]), "C:/w");
    await index.RefreshAsync(Ct);
    Assert.Equal("src/AgentSession.cs", index.Search("agentsession")[0].Name);
  }

  [Fact]
  public async Task CapsResults()
  {
    WorkspaceFileIndex index = new(
      new FakeSearch([.. Enumerable.Range(0, 200).Select(i => $"C:/w/f{i}.cs")]), "C:/w");
    await index.RefreshAsync(Ct);
    Assert.Equal(50, index.Search("f").Count);
  }

  [Fact]
  public async Task UsesForwardSlashesInNames()
  {
    WorkspaceFileIndex index = new(new FakeSearch(["C:/w/src/A/B.cs"]), "C:/w");
    await index.RefreshAsync(Ct);
    Assert.Equal("src/A/B.cs", index.Search("B").Single().Name);
  }

  [Fact]
  public async Task AQueryFindsADeepFileTheEmptyQueryCapWouldCrowdOut()
  {
    // The regression this pins: ranking the whole catalog under an EMPTY query caps to the
    // 50 shortest paths before any filter runs, so a deep source file is unreachable at every
    // query. Filtering first is what makes the popup able to find it.
    List<string> paths = [.. Enumerable.Range(0, 200).Select(i => $"C:/w/f{i}.log")];
    paths.Add("C:/w/src/Deep/Nested/ReferenceInjector.cs");
    WorkspaceFileIndex index = new(new FakeSearch(paths), "C:/w");
    await index.RefreshAsync(Ct);

    ReferenceOption found = Assert.Single(index.Search("ReferenceInjector"));
    Assert.Equal("src/Deep/Nested/ReferenceInjector.cs", found.Name);
  }

  [Fact]
  public void SearchBeforeRefreshReturnsEmpty()
  {
    WorkspaceFileIndex index = new(new FakeSearch(["C:/w/a.cs"]), "C:/w");
    Assert.Empty(index.Search("a"));
  }

  [Fact]
  public async Task ARefreshFailureKeepsThePreviousCache()
  {
    FakeSearch search = new(["C:/w/a.cs"]) { Fail = true };
    WorkspaceFileIndex index = new(search, "C:/w");
    await index.RefreshAsync(Ct);
    Assert.Empty(index.Search("a"));

    search.Fail = false;
    await index.RefreshAsync(Ct);
    Assert.Equal("a.cs", Assert.Single(index.Search("a")).Name);

    search.Fail = true;
    await index.RefreshAsync(Ct);
    Assert.Equal("a.cs", Assert.Single(index.Search("a")).Name);
  }
}
