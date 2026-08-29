using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

public class WorkingDiffToolTests
{
  private const string Root = @"C:\ws";
  private const string SubFile = @"C:\ws\sub\a.cs";

  private static WorkingDiffTool Make(Result<GitDiff> diff, out FakeGitQueryAccess fake)
  {
    fake = new FakeGitQueryAccess(diff);
    return new WorkingDiffTool(new WorkspacePathResolver(Root), fake);
  }

  private static Result<GitDiff> Ok(
      int files = 2, int additions = 3, int deletions = 1,
      string? patch = null, bool truncated = false, int totalChars = 0) =>
      Result.Success(new GitDiff(
          new GitDiffStats(files, additions, deletions),
          patch ?? "diff --git a/x.cs b/x.cs\nindex 111..222 100644\n--- a/x.cs\n+++ b/x.cs\n",
          truncated,
          totalChars == 0 ? (patch ?? "diff").Length : totalChars));

  // ---- Input contract ----

  [Fact]
  public async Task MissingScope_ReturnsError()
  {
    WorkingDiffTool tool = Make(Ok(), out _);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("working_diff", /*lang=json,strict*/ "{\"timeoutSeconds\":120}"), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("MissingParameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("'scope'", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task WrongTypeScope_Rejected()
  {
    WorkingDiffTool tool = Make(Ok(), out _);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("working_diff",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"scope":42}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("InvalidParameterType", result.Content, StringComparison.Ordinal);
    Assert.Contains("string", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task InvalidScopeValue_Rejected()
  {
    WorkingDiffTool tool = Make(Ok(), out _);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("working_diff",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"scope":"Both"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("InvalidParameterValue", result.Content, StringComparison.Ordinal);
    Assert.Contains("Staged", result.Content, StringComparison.Ordinal);
    Assert.Contains("Unstaged", result.Content, StringComparison.Ordinal);
    Assert.Contains("All", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ScopeIsCaseSensitive_LowercaseRejected()
  {
    WorkingDiffTool tool = Make(Ok(), out _);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("working_diff",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"scope":"staged"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("InvalidParameterValue", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task EmptyStringPath_Rejected()
  {
    WorkingDiffTool tool = Make(Ok(), out _);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("working_diff",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"scope":"All","path":""}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("InvalidParameterValue", result.Content, StringComparison.Ordinal);
    Assert.Contains("'path'", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task PathOutsideWorkspace_SurfacesResolverError()
  {
    WorkingDiffTool tool = Make(Ok(), out _);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("working_diff",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"scope":"All","path":"..\\evil.txt"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("PathOutsideWorkspace", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UnknownParameter_Rejected()
  {
    WorkingDiffTool tool = Make(Ok(), out _);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("working_diff",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"scope":"All","stat":true}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Unknown parameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("stat", result.Content, StringComparison.Ordinal);
  }

  // ---- Output contract ----

  [Fact]
  public async Task Success_HeaderMathComesFromFakeStats()
  {
    WorkingDiffTool tool = Make(Ok(files: 2, additions: 3, deletions: 1), out _);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("working_diff",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"scope":"All"}"""), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.StartsWith("[working-diff scope=All path=none: 2 file(s), +3/-1 lines]\n", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Patch_PassedThroughVerbatim()
  {
    string patch = "+hello\tworld\n-old line\n@@ -1 +1 @@\n+new line with <xml> & \"quotes\"\n";
    WorkingDiffTool tool = Make(Ok(patch: patch), out _);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("working_diff",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"scope":"Unstaged"}"""), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal(
        "[working-diff scope=Unstaged path=none: 2 file(s), +3/-1 lines]\n" + patch,
        result.Content);
  }

  [Fact]
  public async Task Truncation_AppendsExactWarningLine()
  {
    string patch = "diff --git a/x.cs b/x.cs\n+truncated tail\n";
    WorkingDiffTool tool = Make(Ok(patch: patch, truncated: true, totalChars: 45123), out _);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("working_diff",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"scope":"All"}"""), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.EndsWith(
        "\n[warning] truncated at 20000 chars; total 45123 — narrow with path/scope",
        result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task NoDifferences_UsesContractLine()
  {
    WorkingDiffTool tool = Make(Ok(files: 0, additions: 0, deletions: 0, patch: ""), out _);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("working_diff",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"scope":"Staged"}"""), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal("[working-diff scope=Staged path=none: no differences]", result.Content);
  }

  // ---- Path resolution and forwarding ----

  [Fact]
  public async Task ResolvedAbsolutePath_ScopeAndRoot_CapturedByFake()
  {
    WorkingDiffTool tool = Make(Ok(), out FakeGitQueryAccess? fake);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("working_diff",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"scope":"Unstaged","path":"sub/a.cs"}"""), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal(Root, fake.RepoPath);
    Assert.Equal(SubFile, fake.Path);
    Assert.Equal("Unstaged", fake.Scope);
    // The header shows the resolved absolute path actually queried.
    Assert.StartsWith($"[working-diff scope=Unstaged path={SubFile}: ", result.Content, StringComparison.Ordinal);
  }

  // ---- Backend errors surface verbatim ----

  [Fact]
  public async Task NotAGitRepository_SurfacesBackendError()
  {
    WorkingDiffTool tool = Make(Result.Failure<GitDiff>(
                new DomainError("NotAGitRepository", $"Not a git repository: {Root}")),
            out _);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("working_diff",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"scope":"All"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains($"Error [NotAGitRepository]: Not a git repository: {Root}", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task GitError_SurfacesBackendError()
  {
    WorkingDiffTool tool = Make(Result.Failure<GitDiff>(
                new DomainError("GitError", "fatal: unable to read tree")),
            out _);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("working_diff",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"scope":"Staged"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [GitError]: fatal: unable to read tree", result.Content, StringComparison.Ordinal);
  }

  private sealed class FakeGitQueryAccess(Result<GitDiff> diff) : IGitQueryAccess
  {
    public string RepoPath { get; private set; } = "";
    public string Scope { get; private set; } = "";
    public string? Path { get; private set; }

    public Task<Result<GitStatus>> GetStatusAsync(string repoPath, CancellationToken ct = default)
        => throw new NotSupportedException("working_diff never queries status.");

    public Task<Result<GitDiff>> GetDiffAsync(string repoPath, string scope, string? path, CancellationToken ct = default)
    {
      RepoPath = repoPath;
      Scope = scope;
      Path = path;
      return Task.FromResult(diff);
    }
  }
}
