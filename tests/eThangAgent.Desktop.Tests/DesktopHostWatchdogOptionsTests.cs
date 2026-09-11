using eThangAgent.Composition;
using eThangAgent.Storage.ACL;

namespace eThangAgent.Desktop.Tests;

/// <summary>Task 3 (config-sqlite): the app-side watchdog runs the CONFIGURED knobs —
///     PrepareAsync ships them from stored app preferences onto DesktopBootstrap so
///     the loop, policy, and RSS monitor never fall back to hardcoded defaults.
///     Joins the Desktop E2E collection: ETHANG_AGENT_DB is process-wide and must
///     never race the other env-var classes.</summary>
[Collection("Desktop E2E")]
public class DesktopHostWatchdogOptionsTests
{
  [Fact]
  public async Task PrepareAsync_ShipsWatchdogOptionsFromStoredSettings()
  {
    string dbPath = Path.Combine(Path.GetTempPath(),
        "ethang-watchdog-opts-" + Guid.NewGuid().ToString("N") + ".db");
    Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", dbPath);
    try
    {
      SqliteAppPreferenceStore seed = new(new AppDatabase());
      _ = await seed.SetAsync(AgentPreferenceKeys.WatchdogTickInterval, "00:00:30",
          TestContext.Current.CancellationToken);
      _ = await seed.SetAsync(AgentPreferenceKeys.WatchdogIdleThreshold, "00:05:00",
          TestContext.Current.CancellationToken);

      DesktopBootstrap boot = await DesktopHost.PrepareAsync();

      Assert.Equal(TimeSpan.FromSeconds(30), boot.WatchdogOptions.TickInterval);
      Assert.Equal(TimeSpan.FromMinutes(5), boot.WatchdogOptions.IdleThreshold);
    }
    finally
    {
      Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", null);
      // Connection pooling keeps the file open after the stores are done with it
      // (the same hazard the sibling PrepareAsync test documents); release first.
      Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
      File.Delete(dbPath);
    }
  }
}
