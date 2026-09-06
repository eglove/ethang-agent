using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Storage.ACL.Tests;

public sealed class SqliteCommandRunStoreTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-cmdrun-{Guid.NewGuid():N}.db");
  private readonly SqliteCommandRunStore _store;

  public SqliteCommandRunStoreTests()
      => _store = new SqliteCommandRunStore(new AppDatabase(_dbPath), "C:\\ws\\alpha");

  public void Dispose()
  {
    GC.SuppressFinalize(this);
    // Named decision (CA1031): temp-db cleanup is best effort.
#pragma warning disable CA1031, S108 // Do not catch general exception types
    try
    {
      File.Delete(_dbPath);
    }
    catch { }
#pragma warning restore CA1031, S108
  }

  [Fact]
  public async Task Add_AssignsMonotonicPerWorkspaceIds_AndRoundTrips()
  {
    Result<CommandRun> first = await _store.AddAsync(new CommandRun(0, "git status", 0, false, "ok", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
    Result<CommandRun> second = await _store.AddAsync(new CommandRun(0, "dotnet test", 1, false, "failed 1", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);

    Assert.True(first.IsSuccess, first.Error?.Message);
    Assert.Equal(1, first.Value.Id);
    Assert.Equal(2, second.Value!.Id);

    Result<CommandRun> back = await _store.GetAsync(2, TestContext.Current.CancellationToken);
    Assert.True(back.IsSuccess, back.Error?.Message);
    Assert.Equal("dotnet test", back.Value.Command);
    Assert.Equal(1, back.Value.ExitCode);
    Assert.False(back.Value.TimedOut);
    Assert.Equal("failed 1", back.Value.Output);
  }

  [Fact]
  public async Task Get_UnknownId_FailsNotFound()
  {
    Result<CommandRun> r = await _store.GetAsync(42, TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal("CommandRunNotFound", r.Error.Code);
  }

  [Fact]
  public async Task Latest_ReturnsHighestIdRun_InThisWorkspace()
  {
    _ = await _store.AddAsync(new CommandRun(0, "a", 0, false, "", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
    _ = await _store.AddAsync(new CommandRun(0, "b", 0, false, "", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);

    Result<CommandRun> latest = await _store.GetLatestAsync(TestContext.Current.CancellationToken);

    Assert.True(latest.IsSuccess, latest.Error?.Message);
    Assert.Equal("b", latest.Value.Command);
  }

  [Fact]
  public async Task Latest_NoRuns_FailsNotFound()
  {
    Result<CommandRun> latest = await _store.GetLatestAsync(TestContext.Current.CancellationToken);

    Assert.False(latest.IsSuccess);
    Assert.Equal("CommandRunNotFound", latest.Error.Code);
  }

  [Fact]
  public async Task Isolation_SecondWorkspace_SeesNeitherIdsNorLatest()
  {
    _ = await _store.AddAsync(new CommandRun(0, "secret", 0, false, "out", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
    SqliteCommandRunStore other = new(new AppDatabase(_dbPath), "C:\\ws\\beta");

    Result<CommandRun> cross = await other.GetAsync(1, TestContext.Current.CancellationToken);
    Result<CommandRun> crossLatest = await other.GetLatestAsync(TestContext.Current.CancellationToken);

    Assert.Equal("CommandRunNotFound", cross.Error!.Code);
    Assert.Equal("CommandRunNotFound", crossLatest.Error!.Code);
  }
}
