using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

/// <summary>A workspace IS its directory path, and the identity table records that binding
///     against a REAL database. These are the tests that keep the accepted cost honest: a
///     directory that is moved or renamed arrives as a NEW workspace, and the records scoped to
///     the old path stay with the old path rather than following the directory.</summary>
public sealed class WorkspaceIdentityDurabilityTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ethang-wsid-{Guid.NewGuid():N}.db");
  private readonly string _root = Path.Combine(Path.GetTempPath(), $"ethang-ws-{Guid.NewGuid():N}");

  public WorkspaceIdentityDurabilityTests() => _ = Directory.CreateDirectory(_root);

  public void Dispose()
  {
    GC.SuppressFinalize(this);
    // Named decision (CA1031): temp cleanup is best effort.
#pragma warning disable CA1031, S108 // Do not catch general exception types
    try
    {
      File.Delete(_dbPath);
      Directory.Delete(_root, recursive: true);
    }
    catch { }
#pragma warning restore CA1031, S108
  }

  private AppDatabase Database => new(_dbPath);

  private static T Must<T>(Result<T> result)
  {
    Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    return result.Value;
  }

  [Fact]
  public async Task OpeningAWorkspace_WritesNothingIntoTheDirectory()
  {
    // The harness adds no artifacts to the user's machine outside its database: opening a
    // workspace leaves the directory exactly as it was.
    AppDatabase database = Database;
    string identity = WorkspaceIdentity.For(_root);
    _ = Must(await new SqliteWorkspaceIdentityStore(database)
        .BindAsync(identity, _root, TestContext.Current.CancellationToken));

    Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
  }

  [Fact]
  public async Task TheIdentity_IsTheDirectoryPath_AndItIsStable()
  {
    AppDatabase database = Database;
    SqliteWorkspaceIdentityStore store = new(database);
    string identity = WorkspaceIdentity.For(_root);
    _ = Must(await store.BindAsync(identity, _root, TestContext.Current.CancellationToken));

    WorkspaceIdentityBinding? found = Must(await store.FindByRootAsync(_root,
        TestContext.Current.CancellationToken));

    Assert.NotNull(found);
    Assert.Equal(identity, found.Id);
    Assert.Equal(identity, found.Root);
  }

  [Fact]
  public async Task AMovedDirectory_IsANewWorkspace_AndTheOldRecordsStayWithTheOldPath()
  {
    // The accepted cost of a path-only identity, pinned: the chain written under the old path
    // is NOT reachable from the new one. Nothing was lost - it is still under the old id - but
    // it does not follow the directory.
    AppDatabase database = Database;
    string originalId = WorkspaceIdentity.For(_root);
    int chainId = await CreateChainAsync(database, originalId);

    string moved = _root + "-moved";
    Directory.Move(_root, moved);
    try
    {
      string movedId = WorkspaceIdentity.For(moved);
      Assert.NotEqual(originalId, movedId);

      Result<Chain> underNewPath = await new SqliteChainStore(database, movedId)
          .GetAsync(chainId, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.False(underNewPath.IsSuccess, "a moved directory must not inherit the old workspace's chains");

      Result<Chain> underOldPath = await new SqliteChainStore(database, originalId)
          .GetAsync(chainId, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(underOldPath.IsSuccess, "the chain stays readable under the path it was written at");
    }
    finally
    {
      Directory.Move(moved, _root);
    }
  }

  [Fact]
  public async Task OrphanQuery_ReportsAChainWhoseWorkspaceIsGone_AndSparesOneWhoseWorkspaceLives()
  {
    // D7: the orphan check spans workspaces by definition, so it must see BOTH - the vanished
    // one reported and the live one spared.
    AppDatabase database = Database;
    string liveId = WorkspaceIdentity.For(_root);
    _ = Must(await new SqliteWorkspaceIdentityStore(database)
        .BindAsync(liveId, _root, TestContext.Current.CancellationToken));
    _ = await CreateChainAsync(database, liveId);

    string vanished = Path.Combine(Path.GetTempPath(), $"ethang-gone-{Guid.NewGuid():N}");
    _ = Directory.CreateDirectory(vanished);
    string goneId = WorkspaceIdentity.For(vanished);
    _ = Must(await new SqliteWorkspaceIdentityStore(database)
        .BindAsync(goneId, vanished, TestContext.Current.CancellationToken));
    int goneChain = await CreateChainAsync(database, goneId);
    Directory.Delete(vanished, recursive: true);

    IReadOnlyList<OrphanedChain> orphaned = Must(await new ChainOrphanQuery(
        new SqliteChainWorkspaceQuery(database)).ListAsync(TestContext.Current.CancellationToken));

    OrphanedChain only = Assert.Single(orphaned);
    Assert.Equal(goneChain, only.ChainId);
    Assert.Equal(vanished, only.LastKnownRoot);
  }

  [Fact]
  public async Task BindAsync_RepointsAnIdentity_AndLeavesNoStaleRow()
  {
    // A stale row on either side would leave two identities fighting over one directory.
    SqliteWorkspaceIdentityStore store = new(Database);
    _ = Must(await store.BindAsync("ws-a", "C:\\work\\one", TestContext.Current.CancellationToken));
    _ = Must(await store.BindAsync("ws-a", "C:\\work\\two", TestContext.Current.CancellationToken));

    Assert.Null(Must(await store.FindByRootAsync("C:\\work\\one", TestContext.Current.CancellationToken)));
    Assert.Equal("ws-a", Must(await store.FindByRootAsync("C:\\work\\two", TestContext.Current.CancellationToken))!.Id);
    _ = Assert.Single(Must(await store.ListAsync(TestContext.Current.CancellationToken)));
  }

  private static async Task<int> CreateChainAsync(AppDatabase database, string workspaceId)
  {
    Chain draft = Chain.New(workspaceId, planId: 1, planVersion: 1, "Chain", sessionId: null,
        stepPositions: [1], now: DateTimeOffset.UtcNow);
    Result<Chain> created = await new SqliteChainStore(database, workspaceId)
        .CreateAsync(draft, TestContext.Current.CancellationToken).ConfigureAwait(false);
    Assert.True(created.IsSuccess, created.Error?.Message ?? "chain create must succeed");
    return created.Value.Id;
  }
}
