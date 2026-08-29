using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>git_commit with the style resolved from the host's commit-style provider:
/// rendering, validation, and staging behavior are unchanged — only the style's origin
/// moved out of model input.</summary>
public class GitCommitToolTests
{
  private const string Root = @"C:\ws";

  private static GitCommitTool Make(Result<GitCommitOutcome> outcome, out FakeGitCommitAccess fake) =>
      Make(CommitStyle.None, outcome, out fake);

  private static GitCommitTool Make(CommitStyle style, Result<GitCommitOutcome> outcome, out FakeGitCommitAccess fake)
  {
    fake = new FakeGitCommitAccess(outcome);
    return new GitCommitTool(new WorkspacePathResolver(Root), fake, new FixedStyleProvider(style));
  }

  private static Result<GitCommitOutcome> OkFor(string message) =>
      Result.Success(new GitCommitOutcome("abc1234", "main", message));

  // ---- Rendering flows through to the commit seam ----

  [Fact]
  public async Task HappyConventionalWithScope_CommitsExactRenderedMessage()
  {
    GitCommitTool tool = Make(CommitStyle.Conventional, OkFor("feat(tools): add git tools\n"), out FakeGitCommitAccess? fake);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"type":"feat","scope":"tools","description":"add git tools"}"""), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal("feat(tools): add git tools\n", fake.Message);
    Assert.Equal(Root, fake.RepoPath);
  }

  [Fact]
  public async Task HappyConventionalWithScope_FormatsAnnotationPlusMessageBlock()
  {
    GitCommitTool tool = Make(CommitStyle.Conventional, OkFor("feat(tools): add git tools\n"), out _);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"type":"feat","scope":"tools","description":"add git tools"}"""), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal("[git-commit abc1234] committed on main\nfeat(tools): add git tools\n", result.Content);
  }

  [Fact]
  public async Task Gitmoji_RendersEmojiIntoCommittedMessage()
  {
    GitCommitTool tool = Make(CommitStyle.Gitmoji, OkFor("\u2728 add git tools\n"), out FakeGitCommitAccess? fake);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"emoji_key":":sparkles:","description":"add git tools"}"""), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal("\u2728 add git tools\n", fake.Message);
  }

  [Fact]
  public async Task ProviderNone_DescriptionStandsAlone()
  {
    GitCommitTool tool = Make(OkFor("wip notes\n"), out FakeGitCommitAccess? fake);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"description":"wip notes"}"""), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal("wip notes\n", fake.Message);
  }

  [Fact]
  public async Task Body_FlowsThroughToCommittedMessage()
  {
    GitCommitTool tool = Make(CommitStyle.Conventional, OkFor("fix(tools): guard input\n\ndetail line\n"), out FakeGitCommitAccess? fake);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"type":"fix","scope":"tools","description":"guard input","body":"detail line"}"""), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal("fix(tools): guard input\n\ndetail line\n", fake.Message);
  }

  // ---- CommitMessage validation codes surface verbatim ----

  [Fact]
  public async Task UnknownType_SurfacesVerbatim()
  {
    GitCommitTool tool = Make(CommitStyle.Conventional, null!, out _);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"type":"banana","description":"x"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [UnknownType]:", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task TypeRequired_SurfacesVerbatim()
  {
    GitCommitTool tool = Make(CommitStyle.Conventional, null!, out _);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"description":"x"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [TypeRequired]:", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ParameterNotAllowed_SurfacesVerbatim()
  {
    GitCommitTool tool = Make(CommitStyle.Conventional, null!, out _);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"type":"feat","emoji_key":":sparkles:","description":"x"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [ParameterNotAllowed]:", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task DescriptionTooLong_SurfacesVerbatim()
  {
    GitCommitTool tool = Make(null!, out _);
    string longDescription = new('a', 73);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
            $$"""{"description":"{{longDescription}}"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [DescriptionTooLong]:", result.Content, StringComparison.Ordinal);
  }

  // ---- Input contract ----

  [Fact]
  public async Task MissingDescription_ReturnsMissingParameter()
  {
    GitCommitTool tool = Make(null!, out _);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("MissingParameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("'description'", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UnknownParameter_Rejected()
  {
    GitCommitTool tool = Make(null!, out _);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"description":"x","author":"me"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Unknown parameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("author", result.Content, StringComparison.Ordinal);
  }

  // ---- files staging sequence ----

  [Fact]
  public async Task FilesProvided_StagesExactPathsBeforeCommitting()
  {
    GitCommitTool tool = Make(OkFor("x\n"), out FakeGitCommitAccess? fake);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"description":"x","files":["a.cs","b/b.txt"]}"""), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal(["stage", "commit"], fake.CallLog);
    Assert.Equal(["a.cs", "b/b.txt"], fake.StagedPaths);
  }

  [Fact]
  public async Task FilesAbsent_NeverStages()
  {
    GitCommitTool tool = Make(OkFor("x\n"), out FakeGitCommitAccess? fake);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"description":"x"}"""), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal(["commit"], fake.CallLog);
  }

  [Fact]
  public async Task StageFailure_AbortsBeforeCommitAndSurfacesError()
  {
    FakeGitCommitAccess fake = new(
        Result.Failure<GitCommitOutcome>(new DomainError("Seed", "never reached")))
    {
      StageOutcome = Result.Failure<bool>(new DomainError("PathspecFailed",
          "fatal: pathspec 'nope.cs' did not match any files")),
    };
    GitCommitTool tool = new(new WorkspacePathResolver(Root), fake, new FixedStyleProvider(CommitStyle.None));
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"description":"x","files":["nope.cs"]}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Equal(["stage"], fake.CallLog);
    Assert.Contains("Error [PathspecFailed]:", result.Content, StringComparison.Ordinal);
  }

  // ---- Backend errors surface verbatim with hints ----

  [Fact]
  public async Task NothingStaged_SurfacesBackendHint()
  {
    GitCommitTool tool = Make(Result.Failure<GitCommitOutcome>(new DomainError("NothingStaged",
                $"The index is empty; there is nothing to commit in {Root}. Stage changes first (e.g. exec: git add <file>).")),
            out _);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"description":"x"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [NothingStaged]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("Stage changes first", result.Content, StringComparison.Ordinal);
  }

  private sealed class FakeGitCommitAccess(Result<GitCommitOutcome> outcome) : IGitCommitAccess
  {
    public List<string> CallLog { get; } = [];
    public List<string> StagedPaths { get; } = [];
    public string RepoPath { get; private set; } = "";
    public string Message { get; private set; } = "";
    public Result<bool> StageOutcome { get; init; } = Result.Success(true);

    public Task<Result<bool>> StageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
      CallLog.Add("stage");
      StagedPaths.AddRange(paths);
      RepoPath = repoPath;
      return Task.FromResult(StageOutcome);
    }

    public Task<Result<GitCommitOutcome>> CommitAsync(string repoPath, string message, CancellationToken ct = default)
    {
      CallLog.Add("commit");
      RepoPath = repoPath;
      Message = message;
      return Task.FromResult(outcome);
    }
  }
}

/// <summary>Test double for the commit-style seam: always serves one fixed style.</summary>
internal sealed class FixedStyleProvider(CommitStyle style) : ICommitStyleProvider
{
  public Task<Result<CommitStyle>> GetAsync(CancellationToken ct = default)
      => Task.FromResult(Result.Success(style));
}
