using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

// Issue #283: git_commit is the ADVISORY attach point. A stale gate annotates; the
// commit still lands. A hard refusal here would push toward --no-verify.
public class GitCommitCompletionNoticeTests
{
  private sealed class FakeCommits : IGitCommitAccess
  {
    public Task<Result<IReadOnlyList<(string Path, DateTimeOffset ModifiedUtc)>>> StatusAsync(
        string repoPath, CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<(string Path, DateTimeOffset ModifiedUtc)>>([]));

    public Task<Result<bool>> StageAsync(string repoPath, IReadOnlyList<string> paths,
        CancellationToken ct = default) => Task.FromResult(Result.Success(true));

    public Task<Result<GitCommitOutcome>> CommitAsync(string repoPath, string message,
        CancellationToken ct = default) =>
        Task.FromResult(Result.Success(new GitCommitOutcome("abc1234", "main", message)));
  }

  private sealed class FixedStyle : ICommitStyleProvider
  {
    public Task<Result<CommitStyle>> GetAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Success(CommitStyle.None));
  }

  private sealed class FixedNotice(string? notice) : ICommitCompletionNotice
  {
    public Task<string?> NoticeAsync(CancellationToken ct = default) => Task.FromResult(notice);
  }

  private static GitCommitTool Tool(ICommitCompletionNotice? notice) =>
      new(new WorkspacePathResolver(Path.GetTempPath()), new FakeCommits(), new FixedStyle(),
          gate: null, notice);

  private static Task<ToolResult> RunCommitAsync(GitCommitTool tool) =>
      tool.ExecuteAsync(new RawToolInput("git_commit",
          /*lang=json,strict*/"{ \"timeoutSeconds\": 30, \"description\": \"work\" }"),
          TestContext.Current.CancellationToken);

  [Fact]
  public async Task StaleGate_AnnotatesButStillCommits()
  {
    ToolResult result = await RunCommitAsync(Tool(new FixedNotice("[warning] the gate is stale")));

    Assert.False(result.IsError);
    Assert.Contains("committed on", result.Content, StringComparison.Ordinal);
    Assert.Contains("the gate is stale", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task FreshGate_AddsNothing()
  {
    ToolResult result = await RunCommitAsync(Tool(new FixedNotice(null)));

    Assert.False(result.IsError);
    Assert.DoesNotContain("[warning]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task NoNoticeWired_OutputUnchanged()
  {
    ToolResult result = await RunCommitAsync(Tool(notice: null));

    Assert.DoesNotContain("[warning]", result.Content, StringComparison.Ordinal);
  }
}
