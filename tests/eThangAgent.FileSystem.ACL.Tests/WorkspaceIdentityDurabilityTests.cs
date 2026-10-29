using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

/// <summary>Issue #317 D5/D6/D7 end to end: the marker file, the identity table, and the orphan
///     check against a REAL database and REAL directories.
///     <para>These are the tests that prove the issue's first acceptance criterion - 'a workspace
///     that is moved is either recoverable or documented as a loss, and the choice is recorded'.
///     The choice recorded is RECOVERABLE, and what makes it true is that a chain written before a
///     move is still found by the same identity after it.</para></summary>
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

  private static WorkspaceIdentityResolver Resolver(AppDatabase database)
      => new(new SqliteWorkspaceIdentityStore(database));

  private static T Must<T>(Result<T> result)
  {
    Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    return result.Value;
  }

  [Fact]
  public async Task Resolve_OnFirstOpen_BindsAnIdentityAndWritesTheMarker()
  {
    WorkspaceIdentityResolution first = Must(await Resolver(Database).ResolveAsync(_root, TestContext.Current.CancellationToken));

    Assert.Equal(WorkspaceIdentityOutcome.Bound, first.Outcome);
    Assert.False(first.IsNoteworthy);
    Assert.True(File.Exists(Path.Combine(_root, WorkspaceIdentityResolver.MarkerDirectory,
        WorkspaceIdentityResolver.MarkerFileName)));
  }

  [Fact]
  public async Task Resolve_Twice_ReturnsTheSameIdentity()
  {
    AppDatabase database = Database;
    string first = Must(await Resolver(database).ResolveAsync(_root, TestContext.Current.CancellationToken)).Id;
    string second = Must(await Resolver(database).ResolveAsync(_root, TestContext.Current.CancellationToken)).Id;

    Assert.Equal(first, second);
  }

  [Fact]
  public async Task Resolve_AfterAMove_KeepsTheIdentity_AndTheChainsFollowIt()
  {
    // D5, the whole point: write a chain under the original path, MOVE the directory, and the
    // chain is still reachable through the same identity.
    AppDatabase database = Database;
    string originalId = Must(await Resolver(database).ResolveAsync(_root, TestContext.Current.CancellationToken)).Id;
    int chainId = await CreateChainAsync(database, originalId);

    string moved = _root + "-moved";
    Directory.Move(_root, moved);
    try
    {
      WorkspaceIdentityResolution afterMove = Must(await Resolver(database).ResolveAsync(moved, TestContext.Current.CancellationToken));

      Assert.Equal(originalId, afterMove.Id);
      Assert.Equal(WorkspaceIdentityOutcome.Moved, afterMove.Outcome);
      Assert.NotNull(afterMove.Notice);

      // The chain is still found: the store scopes on the IDENTITY, which survived the move.
      Result<Chain> chain = await new SqliteChainStore(database, afterMove.Id)
          .GetAsync(chainId, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(chain.IsSuccess, chain.Error?.Message ?? "the chain must follow its workspace");
    }
    finally
    {
      Directory.Move(moved, _root);
    }
  }

  [Fact]
  public async Task Resolve_OfACopiedDirectory_MintsAFreshIdentity_AndTheCopyStartsEmpty()
  {
    // D6: the original still exists, so this is a COPY. Two directories must never share one
    // record set - the copy gets its own identity and sees none of the original's chains.
    AppDatabase database = Database;
    string originalId = Must(await Resolver(database).ResolveAsync(_root, TestContext.Current.CancellationToken)).Id;
    _ = await CreateChainAsync(database, originalId);

    string copy = _root + "-copy";
    CopyDirectory(_root, copy);
    try
    {
      WorkspaceIdentityResolution copied = Must(await Resolver(database).ResolveAsync(copy, TestContext.Current.CancellationToken));

      Assert.NotEqual(originalId, copied.Id);
      Assert.Equal(WorkspaceIdentityOutcome.Copied, copied.Outcome);
      Assert.NotNull(copied.Notice);

      Result<ChainCatalog> catalog = await new SqliteChainStore(database, copied.Id)
          .ListAsync(null, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.Empty(Must(catalog).Chains);

      // And the ORIGINAL keeps its chains: a copy must not steal them either.
      Result<ChainCatalog> original = await new SqliteChainStore(database, originalId)
          .ListAsync(null, TestContext.Current.CancellationToken).ConfigureAwait(true);
      _ = Assert.Single(Must(original).Chains);
    }
    finally
    {
      Directory.Delete(copy, recursive: true);
    }
  }

  [Fact]
  public async Task Resolve_OfAPreIdentityWorkspace_AdoptsThePathAsItsIdentity()
  {
    // A workspace opened before #317 has no marker, and its id IS its path. Minting a new id
    // would orphan every record it already has, so the resolver adopts the existing binding.
    AppDatabase database = Database;
    _ = Must(await new SqliteWorkspaceIdentityStore(database)
        .BindAsync(_root, _root, TestContext.Current.CancellationToken));

    WorkspaceIdentityResolution adopted = Must(await Resolver(database).ResolveAsync(_root, TestContext.Current.CancellationToken));

    Assert.Equal(_root, adopted.Id);
    Assert.Equal(WorkspaceIdentityOutcome.Bound, adopted.Outcome);
  }

  [Fact]
  public async Task Resolve_WithAMalformedMarker_FailsNamed_InsteadOfOrphaningTheWorkspace()
  {
    // A marker that cannot be read is NOT an absent marker: treating it as absent would mint a
    // fresh identity and orphan the workspace's records, which is the data loss #317 prevents.
    string markerDir = Path.Combine(_root, WorkspaceIdentityResolver.MarkerDirectory);
    _ = Directory.CreateDirectory(markerDir);
    await File.WriteAllTextAsync(Path.Combine(markerDir, WorkspaceIdentityResolver.MarkerFileName),
        "{ not json", TestContext.Current.CancellationToken);

    Result<WorkspaceIdentityResolution> result = await Resolver(Database).ResolveAsync(_root, TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("WorkspaceMarkerInvalid", result.Error.Code);
  }

  [Fact]
  public async Task OrphanQuery_ReportsAChainWhoseWorkspaceIsGone_AndSparesOneWhoseWorkspaceLives()
  {
    // D7: the orphan check spans workspaces by definition, so it must see BOTH - the vanished
    // one reported and the live one spared.
    AppDatabase database = Database;
    string liveId = Must(await Resolver(database).ResolveAsync(_root, TestContext.Current.CancellationToken)).Id;
    _ = await CreateChainAsync(database, liveId);

    string vanished = Path.Combine(Path.GetTempPath(), $"ethang-gone-{Guid.NewGuid():N}");
    _ = Directory.CreateDirectory(vanished);
    string goneId = Must(await Resolver(database).ResolveAsync(vanished, TestContext.Current.CancellationToken)).Id;
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
    // A move re-points ONE row. A stale row on either side would leave two identities fighting
    // over one directory, which is what D6 forbids.
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

  private static void CopyDirectory(string source, string destination)
  {
    _ = Directory.CreateDirectory(destination);
    foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
      string relative = Path.GetRelativePath(source, file);
      string target = Path.Combine(destination, relative);
      string? parent = Path.GetDirectoryName(target);
      if (parent is not null)
      {
        _ = Directory.CreateDirectory(parent);
      }

      File.Copy(file, target, overwrite: true);
    }
  }
}
