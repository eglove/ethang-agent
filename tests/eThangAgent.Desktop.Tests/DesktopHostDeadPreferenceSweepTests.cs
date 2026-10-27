using eThangAgent.Storage.ACL;

namespace eThangAgent.Desktop.Tests;

/// <summary>Issue #298 B2: dead preference rows left behind by deleted features are gone
///     once the app opens. The sweep runs in <c>DesktopHost.PrepareAsync</c> - the app owns
///     the schema and is the only writer that migrates, so startup is the one place it can
///     run without fighting the guest child host for the schema lock.
///     Joins the Desktop E2E collection: ETHANG_AGENT_DB is process-wide and must never
///     race the other env-var classes.</summary>
[Collection("Desktop E2E")]
public class DesktopHostDeadPreferenceSweepTests
{
  [Fact]
  public async Task PrepareAsync_Removes_Dead_Preference_Rows_And_Keeps_Live_Ones()
  {
    string dbPath = Path.Combine(Path.GetTempPath(),
        "ethang-dead-prefs-" + Guid.NewGuid().ToString("N") + ".db");
    Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", dbPath);
    try
    {
      SqliteAppPreferenceStore seed = new(new AppDatabase());
      CancellationToken ct = TestContext.Current.CancellationToken;
      _ = await seed.SetAsync("smart_selection:openrouter:C:\\ws\\a", "{}", ct);
      _ = await seed.SetAsync("subagent_remote_host", "false", ct);
      _ = await seed.SetAsync("active_provider", "openrouter", ct);

      _ = await DesktopHost.PrepareAsync();

      SqliteAppPreferenceStore after = new(new AppDatabase());
      Assert.Null(await after.GetAsync("smart_selection:openrouter:C:\\ws\\a", ct));
      Assert.Null(await after.GetAsync("subagent_remote_host", ct));
      Assert.Equal("openrouter", await after.GetAsync("active_provider", ct));
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
