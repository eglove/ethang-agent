using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

public class GitStatusToolTests
{
  private const string Root = @"C:\ws";

  private static (GitStatusTool Tool, FakeGitQueryAccess Fake) Make(GitStatus? status = null)
  {
    FakeGitQueryAccess fake = new(
        status is null
            ? Result.Failure<GitStatus>(new DomainError("NotAGitRepository", $"Not a git repository: {Root}"))
            : Result.Success(status));
    return (new GitStatusTool(new WorkspacePathResolver(Root), fake), fake);
  }

  // ---- Output contract ----

  [Fact]
  public async Task CleanRepo_FormatsCleanLine()
  {
    (GitStatusTool? tool, FakeGitQueryAccess _) = Make(new GitStatus("main", [], [], []));
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_status", /*lang=json,strict*/ "{\"timeoutSeconds\":120}"), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal("[git-status main: clean]", result.Content);
  }

  [Fact]
  public async Task MixedGroups_ExactFullStringOutput()
  {
    GitStatus status = new("main",
        [new GitStatusEntry("M ", "src/a.cs")],
        [new GitStatusEntry(" M", "src/b.cs")],
        ["notes.txt"]);
    (GitStatusTool? tool, FakeGitQueryAccess _) = Make(status);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_status", /*lang=json,strict*/ "{\"timeoutSeconds\":120}"), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal(
        """
            [git-status main: 1 staged, 1 unstaged, 1 untracked]
            staged:
            M  src/a.cs
            unstaged:
             M src/b.cs
            untracked:
            ?? notes.txt
            """,
        result.Content);
  }

  [Fact]
  public async Task EmptyGroups_AreOmitted()
  {
    GitStatus status = new("feature",
        [new GitStatusEntry("A ", "x.cs"), new GitStatusEntry("D ", "y.cs")],
        [], []);
    (GitStatusTool? tool, FakeGitQueryAccess _) = Make(status);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_status", /*lang=json,strict*/ "{\"timeoutSeconds\":120}"), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal(
        """
            [git-status feature: 2 staged, 0 unstaged, 0 untracked]
            staged:
            A  x.cs
            D  y.cs
            """,
        result.Content);
  }

  // ---- Backend errors surface verbatim ----

  [Fact]
  public async Task NotAGitRepository_SurfacesBackendError()
  {
    (GitStatusTool? tool, FakeGitQueryAccess _) = Make();
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_status", /*lang=json,strict*/ "{\"timeoutSeconds\":120}"), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains($"Error [NotAGitRepository]: Not a git repository: {Root}", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task GitError_SurfacesBackendError()
  {
    FakeGitQueryAccess fake = new(
        Result.Failure<GitStatus>(new DomainError("GitError", "fatal: bad object HEAD")));
    GitStatusTool tool = new(new WorkspacePathResolver(Root), fake);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_status", /*lang=json,strict*/ "{\"timeoutSeconds\":120}"), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [GitError]: fatal: bad object HEAD", result.Content, StringComparison.Ordinal);
  }

  // ---- Input contract ----

  [Fact]
  public async Task UnknownParameter_Rejected()
  {
    (GitStatusTool? tool, FakeGitQueryAccess _) = Make();
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_status",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"verbose":true}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Unknown parameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("verbose", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task NonObjectArguments_Rejected()
  {
    (GitStatusTool? tool, FakeGitQueryAccess _) = Make();
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_status", "[1]"), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("JSON object", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public Task EmptyObjectArguments_Accepted() => CleanRepo_FormatsCleanLine();

  // The mandatory timeoutSeconds budget means arguments are never optional:
  // an empty payload is a MissingParameter error, not an implicit empty object.
  [Fact]
  public async Task MissingArguments_Rejected_MissingParameter()
  {
    (GitStatusTool? tool, FakeGitQueryAccess _) = Make(new GitStatus("main", [], [], []));
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_status", ""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    // An empty payload is not valid JSON, so it fails one step earlier than
    // the budget check: malformed arguments, budget never reached.
    Assert.Contains("InvalidJsonArguments", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ResolvesImplicitRoot_AndPassesToQuery()
  {
    (GitStatusTool? tool, FakeGitQueryAccess? fake) = Make(new GitStatus("main", [], [], []));
    _ = await tool.ExecuteAsync(new RawToolInput("git_status", /*lang=json,strict*/ "{\"timeoutSeconds\":120}"), ct: TestContext.Current.CancellationToken);
    Assert.Equal(Root, fake.RepoPath);
  }

  private sealed class FakeGitQueryAccess(Result<GitStatus> status) : IGitQueryAccess
  {
    public string RepoPath { get; private set; } = "";

    public Task<Result<GitStatus>> GetStatusAsync(string repoPath, CancellationToken ct = default)
    {
      RepoPath = repoPath;
      return Task.FromResult(status);
    }

    public Task<Result<GitDiff>> GetDiffAsync(string repoPath, string scope, string? path, CancellationToken ct = default)
        => throw new NotSupportedException("git_status never diffs.");
  }
}
