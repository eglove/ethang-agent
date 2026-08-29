using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>git_commit resolves its CommitStyle through the ICommitStyleProvider seam
/// at execution time. A fake provider pins: the resolved style drives validation
/// (wrong-style parameters are rejected with the same codes as before), the style
/// itself is never taken from model input, and a provider failure surfaces as a tool
/// error instead of a default.</summary>
public class GitCommitToolStyleResolutionTests
{
  private const string Root = @"C:\ws";

  private static GitCommitTool Make(ICommitStyleProvider styles, out RecordingCommitAccess fake)
  {
    fake = new RecordingCommitAccess(Result.Success(new GitCommitOutcome("abc1234", "main", "m\n")));
    return new GitCommitTool(new WorkspacePathResolver(Root), fake, styles);
  }

  [Fact]
  public async Task StyleParameter_IsRejectedAsUnknown()
  {
    GitCommitTool tool = Make(new FixedStyleProvider(CommitStyle.None), out _);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
        /*lang=json,strict*/
        "{\"timeoutSeconds\":120,\"style\":\"None\",\"description\":\"x\"}"), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Unknown parameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("style", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ProviderSaysNone_DescriptionAloneCommits()
  {
    GitCommitTool tool = Make(new FixedStyleProvider(CommitStyle.None), out RecordingCommitAccess fake);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
        /*lang=json,strict*/
        "{\"timeoutSeconds\":120,\"description\":\"wip notes\"}"), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal("wip notes\n", fake.Message);
  }

  [Fact]
  public async Task ProviderSaysConventional_TypeStillRequired()
  {
    GitCommitTool tool = Make(new FixedStyleProvider(CommitStyle.Conventional), out _);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
        /*lang=json,strict*/
        "{\"timeoutSeconds\":120,\"description\":\"no type\"}"), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [TypeRequired]:", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ProviderSaysConventional_FullValidInputCommits()
  {
    GitCommitTool tool = Make(new FixedStyleProvider(CommitStyle.Conventional), out RecordingCommitAccess fake);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
        /*lang=json,strict*/
        "{\"timeoutSeconds\":120,\"type\":\"feat\",\"scope\":\"tools\",\"description\":\"add git tools\"}"), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal("feat(tools): add git tools\n", fake.Message);
  }

  [Fact]
  public async Task ProviderSaysGitmoji_EmojiKeyStillRequired()
  {
    GitCommitTool tool = Make(new FixedStyleProvider(CommitStyle.Gitmoji), out _);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
        /*lang=json,strict*/
        "{\"timeoutSeconds\":120,\"description\":\"no emoji\"}"), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [EmojiKeyRequired]:", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ProviderFails_TypedErrorSurfaces_NoCommitHappens()
  {
    GitCommitTool tool = Make(new FailingStyleProvider(), out RecordingCommitAccess fake);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("git_commit",
        /*lang=json,strict*/
        "{\"timeoutSeconds\":120,\"description\":\"x\"}"), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [InvalidStoredStyle]:", result.Content, StringComparison.Ordinal);
    Assert.Empty(fake.CallLog);
  }

  private sealed class RecordingCommitAccess(Result<GitCommitOutcome> outcome) : IGitCommitAccess
  {
    public List<string> CallLog { get; } = [];
    public string Message { get; private set; } = "";

    public Task<Result<bool>> StageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
      CallLog.Add("stage");
      return Task.FromResult(Result.Success(true));
    }

    public Task<Result<GitCommitOutcome>> CommitAsync(string repoPath, string message, CancellationToken ct = default)
    {
      CallLog.Add("commit");
      Message = message;
      return Task.FromResult(outcome);
    }
  }

  private sealed class FixedStyleProvider(CommitStyle style) : ICommitStyleProvider
  {
    public Task<Result<CommitStyle>> GetAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success(style));
  }

  private sealed class FailingStyleProvider : ICommitStyleProvider
  {
    public Task<Result<CommitStyle>> GetAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Failure<CommitStyle>(
            new DomainError("InvalidStoredStyle", "stored 'bogus' is not a valid commit style")));
  }
}
