using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain.Verification;

namespace eThangAgent.ToolDomain.Tests.Verification;

public class GitCommitGateTests
{
  private static readonly DateTimeOffset Base = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);

  private sealed class FakeLedger : IVerificationLedger
  {
    public List<ShellExecutionRecord> Records { get; } = [];
    public void Append(ShellExecutionRecord record) => Records.Add(record);
    public IReadOnlyList<ShellExecutionRecord> Snapshot() => [.. Records];
  }

  private sealed class FakeCommits : IGitCommitAccess
  {
    public int CommitCalls { get; private set; }
    public bool FailStatus { get; init; }
    public IReadOnlyList<(string Path, DateTimeOffset ModifiedUtc)> Files { get; init; } =
        [("a.cs", Base)];

    public Task<Result<bool>> StageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default) =>
        Task.FromResult(Result.Success(true));

    public Task<Result<GitCommitOutcome>> CommitAsync(string repoPath, string message, CancellationToken ct = default)
    {
      CommitCalls++;
      return Task.FromResult(Result.Success(new GitCommitOutcome("abc1234", "main", "x")));
    }

    public Task<Result<IReadOnlyList<(string Path, DateTimeOffset ModifiedUtc)>>> StatusAsync(
        string repoPath, CancellationToken ct = default)
    {
      IReadOnlyList<(string Path, DateTimeOffset ModifiedUtc)> files = Files;
      return Task.FromResult(FailStatus
          ? Result.Failure<IReadOnlyList<(string Path, DateTimeOffset ModifiedUtc)>>(new DomainError("GitFailed", "down"))
          : Result.Success(files));
    }
  }

  private static VerificationCommandSpecification Classifier() => new(["dotnet test"]);

  private static GitCommitTool MakeTool(FakeLedger ledger, FakeCommits commits, bool enabled = true)
  {
    VerificationGate gate = new(ledger, new VerificationFreshnessSpecification(Classifier()), enabled);
    return new GitCommitTool(
        new WorkspacePathResolver("C:\\repo"),
        commits,
        new FixedStyle(),
        gate);
  }

  private static Task<ToolResult> RunCommit(GitCommitTool tool) =>
      tool.ExecuteAsync(
          new RawToolInput("git_commit",
              /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"description\":\"x\"}"),
          ct: TestContext.Current.CancellationToken);

  [Fact]
  public async Task DirtyTree_StaleLedger_CommitsWithWarning()
  {
    FakeLedger ledger = new();
    FakeCommits commits = new();
    GitCommitTool tool = MakeTool(ledger, commits);

    ToolResult result = await RunCommit(tool);

    Assert.False(result.IsError);
    Assert.StartsWith("[git-commit abc1234]", result.Content, StringComparison.Ordinal);
    Assert.Contains("[warning]", result.Content, StringComparison.Ordinal);
    Assert.Contains("1 file(s) changed", result.Content, StringComparison.Ordinal);
    Assert.Contains("last verification: none", result.Content, StringComparison.Ordinal);
    Assert.Equal(1, commits.CommitCalls);
  }

  [Fact]
  public async Task FreshVerification_CommitProceeds()
  {
    FakeLedger ledger = new();
    ledger.Append(new ShellExecutionRecord(["dotnet", "test"], 0, Base.AddMinutes(5), Base.AddMinutes(6)));
    FakeCommits commits = new();
    GitCommitTool tool = MakeTool(ledger, commits);

    ToolResult result = await RunCommit(tool);

    Assert.False(result.IsError);
    Assert.StartsWith("[git-commit abc1234]", result.Content, StringComparison.Ordinal);
    Assert.Equal(1, commits.CommitCalls);
  }

  [Fact]
  public async Task DirtyTree_StaleLedger_WarningCarriesCountsAndLastRun()
  {
    FakeLedger ledger = new();
    ledger.Append(new ShellExecutionRecord(["dotnet", "build"], 0, Base.AddMinutes(-30), Base.AddMinutes(-29)));
    FakeCommits commits = new();
    GitCommitTool tool = MakeTool(ledger, commits);

    ToolResult result = await RunCommit(tool);

    Assert.False(result.IsError);
    Assert.Equal(1, commits.CommitCalls);
    Assert.Contains("dotnet build", result.Content, StringComparison.Ordinal);
    Assert.Contains("No successful verification-class command started after the newest change", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task StatusFailure_GateStandsDown_CommitProceeds()
  {
    FakeLedger ledger = new();
    FakeCommits commits = new() { FailStatus = true };
    GitCommitTool tool = MakeTool(ledger, commits);

    ToolResult result = await RunCommit(tool);

    Assert.False(result.IsError);
    Assert.Equal(1, commits.CommitCalls);
  }

  [Fact]
  public async Task NothingChanged_Proceeds_WithEmptyLedger()
  {
    FakeLedger ledger = new();
    FakeCommits commits = new() { Files = [] };
    GitCommitTool tool = MakeTool(ledger, commits);

    ToolResult result = await RunCommit(tool);

    Assert.False(result.IsError);
    Assert.Equal(1, commits.CommitCalls);
  }

  [Fact]
  public async Task DisabledGate_Proceeds_WithoutLedger()
  {
    FakeLedger ledger = new();
    FakeCommits commits = new();
    VerificationGate gate = new(ledger, new VerificationFreshnessSpecification(Classifier()), enabled: false);
    GitCommitTool tool = new(new WorkspacePathResolver("C:\\repo"), commits, new FixedStyle(), gate);

    ToolResult result = await RunCommit(tool);

    Assert.False(result.IsError);
    Assert.Equal(1, commits.CommitCalls);
  }

  private sealed class FixedStyle : ICommitStyleProvider
  {
    public Task<Result<CommitStyle>> GetAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Success(CommitStyle.None));
  }
}
