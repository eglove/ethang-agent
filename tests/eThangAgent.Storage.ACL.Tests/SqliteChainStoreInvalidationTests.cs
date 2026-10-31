using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Issue #304 D5 at the store: <c>chain_steps.invalidated_by</c> round-trips, and
///     NULL means 'not invalidated' - the ordinary case, and a FACT rather than a guess because
///     every step that predates the column is genuinely un-invalidated.</summary>
public sealed class SqliteChainStoreInvalidationTests : IDisposable
{
  private const string Ws = "C:\\ws\\alpha";
  private static readonly DateTimeOffset T0 = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);
  private readonly string _dbPath =
      Path.Combine(Path.GetTempPath(), $"ethang-chain-inv-{Guid.NewGuid():N}.db");

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

  private SqliteChainStore NewStore() => new(new AppDatabase(_dbPath), Ws);

  private static T Must<T>(Result<T> result)
  {
    Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    return result.Value;
  }

  private static Chain Draft()
      => Chain.New(Ws, 12, 4, "Chain title", "session-root", [1, 2], T0);

  // THE ROUND TRIP: the refutation key survives a save and a load, and the status does NOT
  // change - the two facts D5 keeps apart.
  [Fact]
  public async Task InvalidateStep_RoundTripsTheRefutationAndKeepsTheStatus()
  {
    SqliteChainStore store = NewStore();
    Chain created = Must(await store.CreateAsync(Draft(), TestContext.Current.CancellationToken));
    Chain running = Must(await store.SaveAsync(
        created.ClaimStep(1, "session-a", T0), created.Version,
        TestContext.Current.CancellationToken));
    Chain done = Must(await store.SaveAsync(
        running.SettleStep(1, ChainStepStatus.Done, ChainActor.Session("session-a"), "ok", T0),
        running.Version, TestContext.Current.CancellationToken));

    _ = Must(await store.SaveAsync(
        done.InvalidateStep(1, "refutations/slug-p1", T0), done.Version,
        TestContext.Current.CancellationToken));

    Chain loaded = Must(await store.GetAsync(created.Id, TestContext.Current.CancellationToken));
    ChainStep step = loaded.Steps[0];
    Assert.Equal(ChainStepStatus.Done, step.Status);
    Assert.Equal("refutations/slug-p1", step.InvalidatedBy);
  }

  // NULL IS THE ORDINARY CASE: an un-invalidated step reads back with no marker, never a
  // fabricated one.
  [Fact]
  public async Task AnUninvalidatedStep_ReadsBackWithNoMarker()
  {
    SqliteChainStore store = NewStore();
    _ = Must(await store.CreateAsync(Draft(), TestContext.Current.CancellationToken));

    Chain loaded = Must(await store.GetAsync(1, TestContext.Current.CancellationToken));

    Assert.All(loaded.Steps, step => Assert.False(step.IsInvalidated));
    Assert.Null(loaded.Steps[0].InvalidatedBy);
  }

  // Clearing the markers round-trips too, which is what makes the human's exit real rather
  // than a view-layer illusion.
  [Fact]
  public async Task ClearInvalidation_RoundTrips()
  {
    SqliteChainStore store = NewStore();
    Chain created = Must(await store.CreateAsync(Draft(), TestContext.Current.CancellationToken));
    Chain invalidated = Must(await store.SaveAsync(
        created.InvalidateStep(1, "refutations/slug-p1", T0), created.Version,
        TestContext.Current.CancellationToken));

    _ = Must(await store.SaveAsync(invalidated.ClearInvalidation(T0), invalidated.Version,
        TestContext.Current.CancellationToken));

    Chain loaded = Must(await store.GetAsync(created.Id, TestContext.Current.CancellationToken));
    Assert.False(loaded.Steps[0].IsInvalidated);
  }
}
