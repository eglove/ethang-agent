using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>A chain step's group and the git worktree its session runs in are part of the
///     durable record: the reconciler reads the members' worktrees after every member's session
///     has closed, so a fact held only in memory is a fact the merge cannot find.</summary>
public sealed class SqliteChainStoreGroupTests : IDisposable
{
  private const string Ws = "C:\\ws\\alpha";
  private static readonly DateTimeOffset T0 = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);
  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-chaingroup-{Guid.NewGuid():N}.db");

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

  /// <summary>Asserts success and yields the value, so every test reads as the chain it
  ///     operates on rather than a null-check ladder.</summary>
  private static T Must<T>(Result<T> result)
  {
    Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    return result.Value;
  }

  /// <summary>Two members in group 1 and the reconciler at position 3 - the declaration
  ///     chain creation accepts for a group.</summary>
  private static Chain GroupedDraft() => Chain.NewFromSeeds(Ws, 12, 4, "Grouped chain",
      "session-root",
      [
        new ChainStepSeed(1, ChainStepRole.Implement, Group: 1),
        new ChainStepSeed(2, ChainStepRole.Implement, Group: 1),
        ChainStepSeed.Implementing(3),
      ],
      T0);

  [Fact]
  public async Task SaveAsync_RoundTripsTheGroupAndTheWorktree()
  {
    SqliteChainStore store = NewStore();
    Chain created = Must(await store.CreateAsync(GroupedDraft(),
        TestContext.Current.CancellationToken));
    Chain withWorktree = created.WithWorktree(1, "C:/ws/.worktrees/chain-1-step-1", T0);

    _ = Must(await store.SaveAsync(withWorktree, created.Version,
        TestContext.Current.CancellationToken));
    Chain reloaded = Must(await store.GetAsync(created.Id, TestContext.Current.CancellationToken));

    Assert.Equal(1, reloaded.StepAt(1)!.Group);
    Assert.Equal("C:/ws/.worktrees/chain-1-step-1", reloaded.StepAt(1)!.WorktreePath);
    // The sibling is in the same group and has no worktree recorded yet: the two facts are
    // independent columns, not one group-shaped value.
    Assert.Equal(1, reloaded.StepAt(2)!.Group);
    Assert.Null(reloaded.StepAt(2)!.WorktreePath);
    // The reconciler runs alone, in the workspace checkout.
    Assert.Null(reloaded.StepAt(3)!.Group);
    Assert.Null(reloaded.StepAt(3)!.WorktreePath);
  }

  [Fact]
  public async Task CreateAsync_PersistsTheDeclaredGroup()
  {
    SqliteChainStore store = NewStore();
    Chain created = Must(await store.CreateAsync(GroupedDraft(),
        TestContext.Current.CancellationToken));

    Chain reloaded = Must(await store.GetAsync(created.Id, TestContext.Current.CancellationToken));

    Assert.Equal([1, 2], reloaded.GroupMembers(1).Select(step => step.Position));
    Assert.Empty(reloaded.GroupMembers(2));
  }

  [Fact]
  public async Task WithWorktree_RecordsThePathOnTheStepAtThatPositionOnly()
  {
    Chain created = Must(await NewStore().CreateAsync(GroupedDraft(),
        TestContext.Current.CancellationToken));

    Chain stamped = created.WithWorktree(2, "C:/ws/.worktrees/chain-1-step-2", T0);

    Assert.Equal("C:/ws/.worktrees/chain-1-step-2", stamped.StepAt(2)!.WorktreePath);
    Assert.Null(stamped.StepAt(1)!.WorktreePath);
    Assert.Equal(T0, stamped.UpdatedAt);
  }
}
