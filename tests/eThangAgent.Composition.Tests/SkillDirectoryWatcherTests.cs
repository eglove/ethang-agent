using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.Composition.Tests;

/// <summary>The hot-reload loop (spec #26): the watcher starts inert, reloads on
/// request, announces through the reloader, and stays quiet while nothing changed.
/// Timing behavior (FSW events) is integration-tested over real temp directories;
/// these tests pin the decision logic.</summary>
public class SkillDirectoryWatcherTests
{
  [Fact]
  public async Task CheckOnce_NoChange_NoAnnouncementAndListUnchanged()
  {
    CountingCatalog catalog = new();
    int announced = 0;
    SkillDirectoryWatcher watcher = new(catalog, diff => announced++);
    watcher.Start();
    try
    {
      await watcher.CheckOnceAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.Equal(0, announced);
      Assert.Equal(1, catalog.ReloadCalls);
    }
    finally
    {
      await watcher.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task CheckOnce_ChangeDetectedByCatalog_AnnouncesTheDiff()
  {
    SkillReloadDiff diff = new([Mk("added")], [], []);
    CountingCatalog catalog = new() { NextDiff = diff };
    int announced = 0;
    SkillDirectoryWatcher watcher = new(catalog, _ => announced++);
    watcher.Start();
    try
    {
      await watcher.CheckOnceAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.Equal(1, announced);
      Assert.Equal(1, catalog.ReloadCalls);
    }
    finally
    {
      await watcher.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task CheckOnce_ConcurrentCalls_Sequentialized()
  {
    CountingCatalog catalog = new() { ReloadDelay = TimeSpan.FromMilliseconds(50) };
    SkillDirectoryWatcher watcher = new(catalog, _ => { });
    watcher.Start();
    try
    {
      // Both calls overlap (each pass takes ~50 ms); under the former
      // skip-when-in-flight interlock the second call returned without
      // reloading — the defect the reload E2E caught. Serialized contract:
      // EVERY caller performs its own pass.
      Task a = watcher.CheckOnceAsync(TestContext.Current.CancellationToken);
      Task b = watcher.CheckOnceAsync(TestContext.Current.CancellationToken);
      await a.ConfigureAwait(true);
      await b.ConfigureAwait(true);
      Assert.Equal(2, catalog.ReloadCalls);
    }
    finally
    {
      await watcher.DisposeAsync().ConfigureAwait(true);
    }
  }

  private static SkillDefinition Mk(string name) => new(
      name, "d", "b", Version: 1, SkillSource.File, ProvenanceSessionId: null,
      CreatedAt: DateTimeOffset.UnixEpoch, UpdatedAt: DateTimeOffset.UnixEpoch,
      Manual: false, Origin: null);

  private sealed class CountingCatalog : IReloadableSkillCatalog
  {
    public int ReloadCalls { get; private set; }
    public SkillReloadDiff NextDiff { get; set; } = new([], [], []);
    public TimeSpan ReloadDelay { get; set; } = TimeSpan.Zero;

    public async Task<Result<SkillReloadDiff>> ReloadAsync(CancellationToken ct = default)
    {
      ReloadCalls++;
      if (ReloadDelay > TimeSpan.Zero)
      {
        await Task.Delay(ReloadDelay, ct).ConfigureAwait(false);
      }

      return Result.Success(NextDiff);
    }
  }
}
