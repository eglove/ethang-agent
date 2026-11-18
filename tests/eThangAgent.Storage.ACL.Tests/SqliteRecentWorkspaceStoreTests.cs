using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>The launcher's recent-workspace list: a directory appears at most once, newest
///     first, and the list survives a restart because it is rows in the app database.</summary>
public sealed class SqliteRecentWorkspaceStoreTests : IDisposable
{
  private readonly string _dbPath = TemplateDatabase.Copy();

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

  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  private SqliteRecentWorkspaceStore Store() => new(new AppDatabase(_dbPath));

  [Fact]
  public async Task Record_KeepsOneRowPerDirectory_NewestFirst()
  {
    SqliteRecentWorkspaceStore store = Store();

    _ = await store.RecordAsync("C:\\work\\alpha", Ct);
    _ = await store.RecordAsync("C:\\work\\beta", Ct);
    _ = await store.RecordAsync("C:\\work\\alpha", Ct);

    Result<IReadOnlyList<RecentWorkspace>> listed = await store.ListAsync(10, Ct);
    Assert.True(listed.IsSuccess);
    Assert.Equal(2, listed.Value.Count);
    Assert.Equal("C:\\work\\alpha", listed.Value[0].Root);
    Assert.Equal("C:\\work\\beta", listed.Value[1].Root);
  }

  [Fact]
  public async Task Record_TreatsATrailingSeparatorAsTheSameDirectory()
  {
    SqliteRecentWorkspaceStore store = Store();

    _ = await store.RecordAsync("C:\\work\\alpha", Ct);
    _ = await store.RecordAsync("C:\\work\\alpha\\", Ct);

    Result<IReadOnlyList<RecentWorkspace>> listed = await store.ListAsync(10, Ct);
    RecentWorkspace only = Assert.Single(listed.Value!);
    Assert.Equal("C:\\work\\alpha", only.Root);
  }

  [Fact]
  public async Task List_ReturnsAtMostTheRequestedLimit()
  {
    SqliteRecentWorkspaceStore store = Store();
    for (int i = 0; i < 25; i++)
    {
      _ = await store.RecordAsync($"C:\\work\\ws{i:D2}", Ct);
    }

    Result<IReadOnlyList<RecentWorkspace>> listed = await store.ListAsync(10, Ct);

    Assert.Equal(10, listed.Value!.Count);
    Assert.Equal("C:\\work\\ws24", listed.Value[0].Root);
  }

  [Fact]
  public async Task Record_RefusesABlankRoot()
  {
    SqliteRecentWorkspaceStore store = Store();

    Result<bool> recorded = await store.RecordAsync("  ", Ct);

    Assert.False(recorded.IsSuccess);
    Assert.Equal("InvalidWorkspaceRoot", recorded.Error?.Code);
    Assert.Empty((await store.ListAsync(10, Ct)).Value!);
  }

  [Fact]
  public async Task List_RefusesALimitBelowOne()
  {
    SqliteRecentWorkspaceStore store = Store();

    Result<IReadOnlyList<RecentWorkspace>> listed = await store.ListAsync(0, Ct);

    Assert.False(listed.IsSuccess);
    Assert.Equal("InvalidLimit", listed.Error?.Code);
  }

  [Fact]
  public async Task Forget_RemovesTheRow_AndSucceedsWhenAbsent()
  {
    SqliteRecentWorkspaceStore store = Store();
    _ = await store.RecordAsync("C:\\work\\alpha", Ct);

    Result<bool> forgotten = await store.ForgetAsync("C:\\work\\alpha", Ct);
    Result<bool> again = await store.ForgetAsync("C:\\work\\alpha", Ct);

    Assert.True(forgotten.IsSuccess);
    Assert.True(again.IsSuccess);
    Assert.Empty((await store.ListAsync(10, Ct)).Value!);
  }

  [Fact]
  public async Task List_SurvivesAReopenedDatabase()
  {
    _ = await Store().RecordAsync("C:\\work\\alpha", Ct);

    Result<IReadOnlyList<RecentWorkspace>> listed = await Store().ListAsync(10, Ct);

    RecentWorkspace only = Assert.Single(listed.Value!);
    Assert.Equal("C:\\work\\alpha", only.Root);
  }
}
