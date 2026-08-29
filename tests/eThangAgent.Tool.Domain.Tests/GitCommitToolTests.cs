using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

public class GitCommitToolTests
{
  private const string Root = @"C:\ws";

  private static GitCommitTool Make(Result<GitCommitOutcome> outcome, out FakeGitCommitAccess fake)
  {
    fake = new FakeGitCommitAccess(outcome);
    return new GitCommitTool(new WorkspacePathResolver(Root), fake);
  }

  private static Result<GitCommitOutcome> OkFor(string message) =>
      Result.Success(new GitCommitOutcome("abc1234", "main", message));

  // ---- Rendering flows through to the commit seam ----

  [Fact]
  public async Task HappyConventionalWithScope_CommitsExactRenderedMessage()
  {
    GitCommitTool tool = Make(OkFor("feat(tools): add git tools\n"), out FakeGitCommitAccess? fake);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"style":"Conventional","type":"feat","scope":"tools","description":"add git tools"}"""), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal("feat(tools): add git tools\n", fake.Message);
    Assert.Equal(Root, fake.RepoPath);
  }

  [Fact]
  public async Task HappyConventionalWithScope_FormatsAnnotationPlusMessageBlock()
  {
    GitCommitTool tool = Make(OkFor("feat(tools): add git tools\n"), out _);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"style":"Conventional","type":"feat","scope":"tools","description":"add git tools"}"""), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal("[git-commit abc1234] committed on main\nfeat(tools): add git tools\n", result.Content);
  }

  [Fact]
  public async Task Gitmoji_RendersEmojiIntoCommittedMessage()
  {
    GitCommitTool tool = Make(OkFor("\u2728 add git tools\n"), out FakeGitCommitAccess? fake);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"style":"Gitmoji","emoji_key":":sparkles:","description":"add git tools"}"""), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal("\u2728 add git tools\n", fake.Message);
  }

  [Fact]
  public async Task StyleNone_DescriptionStandsAlone()
  {
    GitCommitTool tool = Make(OkFor("wip notes\n"), out FakeGitCommitAccess? fake);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"style":"None","description":"wip notes"}"""), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal("wip notes\n", fake.Message);
  }

  [Fact]
  public async Task Body_FlowsThroughToCommittedMessage()
  {
    GitCommitTool tool = Make(OkFor("fix(tools): guard input\n\ndetail line\n"), out FakeGitCommitAccess? fake);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"style":"Conventional","type":"fix","scope":"tools","description":"guard input","body":"detail line"}"""), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal("fix(tools): guard input\n\ndetail line\n", fake.Message);
  }

  // ---- CommitMessage validation codes surface verbatim ----

  [Fact]
  public async Task InvalidStyle_SurfacesVerbatim()
  {
    GitCommitTool tool = Make(null!, out _);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"style":"Bogus","description":"x"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [InvalidStyle]:", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UnknownType_SurfacesVerbatim()
  {
    GitCommitTool tool = Make(null!, out _);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"style":"Conventional","type":"banana","description":"x"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [UnknownType]:", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task TypeRequired_SurfacesVerbatim()
  {
    GitCommitTool tool = Make(null!, out _);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"style":"Conventional","description":"x"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [TypeRequired]:", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ParameterNotAllowed_SurfacesVerbatim()
  {
    GitCommitTool tool = Make(null!, out _);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"style":"Conventional","type":"feat","emoji_key":":sparkles:","description":"x"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [ParameterNotAllowed]:", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task DescriptionTooLong_SurfacesVerbatim()
  {
    GitCommitTool tool = Make(null!, out _);
    string longDescription = new('a', 73);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
            $$"""{"style":"None","description":"{{longDescription}}"}"""), ct: TestContext.Current.CancellationToken);
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
                                 """{"timeoutSeconds":120,"style":"None"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("MissingParameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("'description'", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MissingStyle_ReturnsMissingParameter()
  {
    GitCommitTool tool = Make(null!, out _);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"description":"x"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("MissingParameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("'style'", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UnknownParameter_Rejected()
  {
    GitCommitTool tool = Make(null!, out _);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"style":"None","description":"x","author":"me"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Unknown parameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("author", result.Content, StringComparison.Ordinal);
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
                                 """{"timeoutSeconds":120,"style":"None","description":"x"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [NothingStaged]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("Stage changes first", result.Content, StringComparison.Ordinal);
  }

  private sealed class FakeGitCommitAccess(Result<GitCommitOutcome> outcome) : IGitCommitAccess
  {
    public string RepoPath { get; private set; } = "";
    public string Message { get; private set; } = "";

    public Task<Result<GitCommitOutcome>> CommitAsync(string repoPath, string message, CancellationToken ct = default)
    {
      RepoPath = repoPath;
      Message = message;
      return Task.FromResult(outcome);
    }
  }
}
