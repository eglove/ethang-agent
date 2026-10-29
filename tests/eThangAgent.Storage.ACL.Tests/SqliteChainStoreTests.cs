using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Issue #300: the chain store round-trips the whole record — plan reference,
///     status, park facts, per-step session/attempts/outcome/actor/claim clock — enforces
///     the same CAS the plan store does, and keeps one workspace's chains invisible to
///     another. Every statement filters on the bound workspace.</summary>
public sealed class SqliteChainStoreTests : IDisposable
{
  private const string Ws = "C:\\ws\\alpha";
  private const string OtherWs = "C:\\ws\\beta";
  private static readonly DateTimeOffset T0 = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);
  private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ethang-chain-{Guid.NewGuid():N}.db");

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

  private SqliteChainStore NewStore(string workspace = Ws) => new(new AppDatabase(_dbPath), workspace);

  private static Chain Draft(params int[] positions)
      => Chain.New(Ws, 12, 4, "Chain title", "session-root",
          positions.Length == 0 ? [1] : positions, T0);

  /// <summary>Asserts success and yields the value, so every test reads as the chain it
  ///     operates on rather than a null-check ladder (Result.Value is only non-null on
  ///     success, and the compiler trusts that claim).</summary>
  private static T Must<T>(Result<T> result)
  {
    Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    return result.Value;
  }

  [Fact]
  public async Task FindBySessionAsync_AnswersTheChainClaimingAStepForTheSession()
  {
    SqliteChainStore store = NewStore();
    Chain created = Must(await store.CreateAsync(Draft(1, 2), TestContext.Current.CancellationToken));
    _ = Must(await store.SaveAsync(created.ClaimStep(2, "session-a", T0), created.Version,
        TestContext.Current.CancellationToken));

    Result<Chain?> found = await store.FindBySessionAsync("session-a",
        TestContext.Current.CancellationToken);

    Assert.True(found.IsSuccess, found.Error?.Message ?? "lookup must succeed");
    Assert.Equal(created.Id, found.Value?.Id);
  }

  [Fact]
  public async Task FindBySessionAsync_ForASessionHoldingNoStep_IsNull()
  {
    SqliteChainStore store = NewStore();
    _ = Must(await store.CreateAsync(Draft(1), TestContext.Current.CancellationToken));

    Result<Chain?> found = await store.FindBySessionAsync("session-stranger",
        TestContext.Current.CancellationToken);

    Assert.True(found.IsSuccess, found.Error?.Message ?? "lookup must succeed");
    Assert.Null(found.Value);
  }

  [Fact]
  public async Task FindBySessionAsync_DoesNotSeeAnotherWorkspacesChain()
  {
    SqliteChainStore alpha = NewStore();
    Chain created = Must(await alpha.CreateAsync(Draft(1), TestContext.Current.CancellationToken));
    _ = Must(await alpha.SaveAsync(created.ClaimStep(1, "session-a", T0), created.Version,
        TestContext.Current.CancellationToken));

    Result<Chain?> found = await NewStore(OtherWs).FindBySessionAsync("session-a",
        TestContext.Current.CancellationToken);

    Assert.True(found.IsSuccess, found.Error?.Message ?? "lookup must succeed");
    Assert.Null(found.Value);
  }

  [Fact]
  public async Task CreateAsync_AssignsIdAndVersionOne_AndRoundTripsEveryStepFact()
  {
    SqliteChainStore store = NewStore();
    Result<Chain> created = await store.CreateAsync(Draft(1, 2), TestContext.Current.CancellationToken);

    Assert.True(created.IsSuccess, created.Error?.Message ?? "create must succeed");
    Assert.Equal(1, created.Value.Version);
    Assert.True(created.Value.Id >= 1);

    Result<Chain> loaded = await store.GetAsync(created.Value.Id, TestContext.Current.CancellationToken);
    Assert.True(loaded.IsSuccess, loaded.Error?.Message ?? "get must succeed");
    Chain chain = loaded.Value;
    Assert.Equal(12, chain.PlanId);
    Assert.Equal(4, chain.PlanVersion);
    Assert.Equal("Chain title", chain.Title);
    Assert.Equal(Ws, chain.WorkspaceId);
    Assert.Equal(ChainStatus.Running, chain.Status);
    Assert.Equal("session-root", chain.SessionId);
    Assert.Equal([1, 2], chain.Steps.Select(s => s.Position));
    Assert.All(chain.Steps, s => Assert.Equal(ChainStepStatus.Pending, s.Status));
  }

  [Fact]
  public async Task SaveAsync_RoundTripsTheClaimAndTheOutcome()
  {
    SqliteChainStore store = NewStore();
    Chain chain = Must(await store.CreateAsync(Draft(1), TestContext.Current.CancellationToken));

    Chain claimed = chain.ClaimStep(1, "session-a", T0);
    Result<Chain> saved = await store.SaveAsync(claimed, chain.Version, TestContext.Current.CancellationToken);
    Assert.True(saved.IsSuccess, saved.Error?.Message ?? "save must succeed");
    Assert.Equal(2, saved.Value.Version);

    ChainStep step = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken)).Steps[0];
    Assert.Equal(ChainStepStatus.Running, step.Status);
    Assert.Equal("session-a", step.SessionId);
    Assert.Equal(1, step.Attempts);
    Assert.Equal(T0, step.ClaimedAt);
    Assert.Equal("session:session-a", step.Actor.Encoded);

    Chain settled = saved.Value.SettleStep(1, ChainStepStatus.Done, ChainActor.Session("session-a"),
        "gate green", T0.AddMinutes(5));
    Result<Chain> settledSaved = await store.SaveAsync(settled, saved.Value.Version,
        TestContext.Current.CancellationToken);
    Assert.True(settledSaved.IsSuccess, settledSaved.Error?.Message ?? "settle must save");

    ChainStep done = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken)).Steps[0];
    Assert.Equal(ChainStepStatus.Done, done.Status);
    Assert.Equal("gate green", done.Outcome);
    Assert.Equal(T0.AddMinutes(5), done.CompletedAt);
    Assert.Null(done.ClaimedAt);
  }

  [Fact]
  public async Task SaveAsync_RoundTripsTheParkFacts()
  {
    SqliteChainStore store = NewStore();
    Chain chain = Must(await store.CreateAsync(Draft(1, 2), TestContext.Current.CancellationToken));

    Chain parked = chain.Park("reviewer denied twice", 2, ChainActor.Harness, T0);
    _ = await store.SaveAsync(parked, chain.Version, TestContext.Current.CancellationToken);

    Chain loaded = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken));
    Assert.Equal(ChainStatus.Parked, loaded.Status);
    Assert.Equal("reviewer denied twice", loaded.ParkReason);
    Assert.Equal(2, loaded.ParkedAtPosition);
  }

  [Fact]
  public async Task SaveAsync_WithAStaleVersion_FailsVersionConflict_NamingTheCurrentVersion()
  {
    SqliteChainStore store = NewStore();
    Chain chain = Must(await store.CreateAsync(Draft(1), TestContext.Current.CancellationToken));
    _ = await store.SaveAsync(chain.ClaimStep(1, "session-a", T0), chain.Version,
        TestContext.Current.CancellationToken);

    Result<Chain> stale = await store.SaveAsync(chain.ClaimStep(1, "session-b", T0), chain.Version,
        TestContext.Current.CancellationToken);

    Assert.False(stale.IsSuccess);
    Assert.Equal("VersionConflict", stale.Error.Code);
    Assert.Contains("current version is 2", stale.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task GetAsync_FromAnotherWorkspace_FailsChainNotFound()
  {
    Chain chain = Must(await NewStore().CreateAsync(Draft(1), TestContext.Current.CancellationToken));

    Result<Chain> crossWorkspace = await NewStore(OtherWs).GetAsync(chain.Id,
        TestContext.Current.CancellationToken);

    Assert.False(crossWorkspace.IsSuccess);
    Assert.Equal("ChainNotFound", crossWorkspace.Error.Code);
  }

  [Fact]
  public async Task ListAsync_IsWorkspaceScoped_AndFiltersByStatus()
  {
    SqliteChainStore store = NewStore();
    Chain running = Must(await store.CreateAsync(Draft(1), TestContext.Current.CancellationToken));
    Chain parked = Must(await store.CreateAsync(Draft(1), TestContext.Current.CancellationToken));
    _ = await store.SaveAsync(parked.Park("why", 1, ChainActor.Harness, T0), parked.Version,
        TestContext.Current.CancellationToken);
    _ = await NewStore(OtherWs).CreateAsync(
        Chain.New(OtherWs, 1, 1, "other", null, [1], T0), TestContext.Current.CancellationToken);

    Result<ChainCatalog> all = await store.ListAsync(null, TestContext.Current.CancellationToken);
    Assert.True(all.IsSuccess, all.Error?.Message ?? "list must succeed");
    Assert.Equal([running.Id, parked.Id], Must(all).Chains.Select(c => c.Id));
    Assert.Empty(all.Value.Corrupt);

    Result<ChainCatalog> onlyParked = await store.ListAsync(ChainStatus.Parked,
        TestContext.Current.CancellationToken);
    Assert.Equal([parked.Id], Must(onlyParked).Chains.Select(c => c.Id));
  }

  [Fact]
  public async Task ListAsync_EmptyWorkspace_ReturnsAnEmptyCatalog()
  {
    Result<ChainCatalog> listed = await NewStore().ListAsync(null, TestContext.Current.CancellationToken);
    Assert.True(listed.IsSuccess, listed.Error?.Message ?? "list must succeed");
    Assert.Empty(listed.Value.Chains);
    Assert.Empty(listed.Value.Corrupt);
  }

  [Fact]
  public async Task ChainService_RunsTheWholeLifecycle_OverTheRealStore()
  {
    SqliteChainStore store = NewStore();
    ChainService service = new(store);
    List<ChainStatusChanged> statusChanges = [];
    List<ChainStepChanged> stepChanges = [];
    service.StatusChanged += statusChanges.Add;
    service.StepChanged += stepChanges.Add;

    Result<Chain> created = await service.CreateAsync(
        new ChainCreation(Ws, 9, 2, "lifecycle", "session-root", [1]), T0,
        TestContext.Current.CancellationToken);
    Assert.True(created.IsSuccess, created.Error?.Message ?? "create must succeed");

    Result<Chain> claimed = await service.ClaimStepAsync(created.Value.Id, 1, "session-a", T0,
        TestContext.Current.CancellationToken);
    Assert.True(claimed.IsSuccess, claimed.Error?.Message ?? "claim must succeed");
    Assert.Equal(1, claimed.Value.Steps[0].Attempts);

    Result<Chain> settled = await service.SettleStepAsync(created.Value.Id, 1, ChainStepStatus.Done,
        ChainActor.Session("session-a"), "done", T0.AddMinutes(1), TestContext.Current.CancellationToken);
    Assert.True(settled.IsSuccess, settled.Error?.Message ?? "settle must succeed");

    Result<Chain> completed = await service.SetStatusAsync(created.Value.Id, ChainStatus.Completed,
        ChainActor.Harness, T0.AddMinutes(2), TestContext.Current.CancellationToken);
    Assert.True(completed.IsSuccess, completed.Error?.Message ?? "completion must succeed");
    Assert.Equal(ChainStatus.Completed, completed.Value.Status);

    Assert.Equal(["claimed", "done"], stepChanges.Select(c => c.Kind));
    _ = Assert.Single(statusChanges);
    Assert.Equal(ChainStatus.Completed, statusChanges[0].To);
    Assert.True(statusChanges[0].Actor.IsHarness);
  }

  [Fact]
  public async Task ChainService_CreateWithNoSteps_FailsInvalidTransition()
  {
    ChainService service = new(NewStore());
    Result<Chain> created = await service.CreateAsync(
        new ChainCreation(Ws, 1, 1, "empty", null, []), T0, TestContext.Current.CancellationToken);

    Assert.False(created.IsSuccess);
    Assert.Equal("InvalidTransition", created.Error.Code);
  }

  [Fact]
  public async Task ChainService_CompletionWithAPendingStep_FailsInvalidTransition()
  {
    ChainService service = new(NewStore());
    Chain chain = Must(await service.CreateAsync(
        new ChainCreation(Ws, 1, 1, "t", null, [1, 2]), T0,
        TestContext.Current.CancellationToken));

    Result<Chain> completed = await service.SetStatusAsync(chain.Id, ChainStatus.Completed,
        ChainActor.Harness, T0, TestContext.Current.CancellationToken);

    Assert.False(completed.IsSuccess);
    Assert.Equal("InvalidTransition", completed.Error.Code);
    Chain reloaded = Must(await service.GetAsync(chain.Id, TestContext.Current.CancellationToken));
    Assert.Equal(ChainStatus.Running, reloaded.Status);
  }

  /// <summary>A DIRECT writer (an exec script's SQL) cannot smuggle an out-of-enum value
  ///     past the CHECK constraints — the same door V20/V21 closed for plans.</summary>
  [Fact]
  public async Task CheckConstraints_RefuseOutOfEnumStatuses_FromADirectWriter()
  {
    Chain chain = Must(await NewStore().CreateAsync(Draft(1), TestContext.Current.CancellationToken));

    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteConnection connection = new($"Data Source={_dbPath}");
#pragma warning restore CA2007
    await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    _ = Assert.Throws<SqliteException>(() => Scalar(connection,
        $"UPDATE chains SET status = 'Cancelled' WHERE id = {chain.Id};"));
    _ = Assert.Throws<SqliteException>(() => Scalar(connection,
        $"UPDATE chain_steps SET status = 'Completed' WHERE chain_id = {chain.Id} AND position = 1;"));
  }

  [Fact]
  public async Task CheckConstraints_AdmitEveryDocumentedStatus()
  {
    Chain chain = Must(await NewStore().CreateAsync(Draft(1), TestContext.Current.CancellationToken));

    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteConnection connection = new($"Data Source={_dbPath}");
#pragma warning restore CA2007
    await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    foreach (ChainStatus status in Enum.GetValues<ChainStatus>())
    {
      _ = Scalar(connection, $"UPDATE chains SET status = '{status}' WHERE id = {chain.Id};");
    }

    foreach (ChainStepStatus status in Enum.GetValues<ChainStepStatus>())
    {
      _ = Scalar(connection,
          $"UPDATE chain_steps SET status = '{status}' WHERE chain_id = {chain.Id} AND position = 1;");
    }
  }

  /// <summary>A row that cannot be materialized is REPORTED, never fatal: one corrupt chain
  ///     must not blank the whole workspace listing (the #279 containment, applied here).</summary>
  [Fact]
  public async Task ListAsync_ReportsACorruptRow_WithoutFailingTheListing()
  {
    SqliteChainStore store = NewStore();
    Chain healthy = Must(await store.CreateAsync(Draft(1), TestContext.Current.CancellationToken));
    Chain broken = Must(await store.CreateAsync(Draft(1), TestContext.Current.CancellationToken));

    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteConnection connection = new($"Data Source={_dbPath}");
#pragma warning restore CA2007
    await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    // The CHECK constraint is the schema's door; this simulates a row written before it
    // existed by dropping the constraint's table content through a raw rebuild.
    _ = Scalar(connection, $"UPDATE chain_steps SET actor = 'nonsense' WHERE chain_id = {broken.Id};");

    Result<ChainCatalog> listed = await store.ListAsync(null, TestContext.Current.CancellationToken);

    Assert.True(listed.IsSuccess, listed.Error?.Message ?? "the listing must survive a corrupt row");
    Assert.Equal([healthy.Id], listed.Value.Chains.Select(c => c.Id));
    CorruptChainRow row = Assert.Single(listed.Value.Corrupt);
    Assert.Equal(broken.Id, row.ChainId);
    Assert.Contains("actor", row.Reason, StringComparison.Ordinal);
  }

  [Fact]
  public async Task GetAsync_OnACorruptRow_FailsCorruptChainRow()
  {
    SqliteChainStore store = NewStore();
    Chain broken = Must(await store.CreateAsync(Draft(1), TestContext.Current.CancellationToken));

    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteConnection connection = new($"Data Source={_dbPath}");
#pragma warning restore CA2007
    await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = Scalar(connection, $"UPDATE chain_steps SET actor = 'nonsense' WHERE chain_id = {broken.Id};");

    Result<Chain> loaded = await store.GetAsync(broken.Id, TestContext.Current.CancellationToken);
    Assert.False(loaded.IsSuccess);
    Assert.Equal("CorruptChainRow", loaded.Error.Code);
  }

  private static object Scalar(SqliteConnection connection, string sql)
  {
    using SqliteCommand command = connection.CreateCommand();
    // Named decision (CA2100): every caller passes a literal from this test file.
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
    command.CommandText = sql;
#pragma warning restore CA2100
    return command.ExecuteScalar()!;
  }
}
