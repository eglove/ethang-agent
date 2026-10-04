using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;

namespace eThangAgent.Composition.Tests;

/// <summary>R3.2 end-to-end through session open: a persisted Running row that no runtime
///     owns is Failed(Interrupted) with an audit row once a session opens — but ONLY
///     rows in the OPENING session's workspace (2026-10-04 scope fix: other workspaces'
///     Running rows are other sessions' live state). Rows owned by the fresh container's
///     runtime and already-terminal rows are untouched.</summary>
[Collection("EnvironmentSensitive")]
public class SessionOpenOrphanRepairTests
{
  private static AgentSettings Settings() => new(
      new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
      new SubAgentOptions(null, 2));

  [Fact]
  public async Task SessionOpen_MarksUnownedRunningRows_Interrupted()
  {
    string dbPath = Path.Combine(Path.GetTempPath(), "ethang-orphan-" + Guid.NewGuid().ToString("N") + ".db");
    Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", dbPath);
    try
    {
      string ws = Directory.CreateTempSubdirectory("ethang-ws").FullName;
      // Seed an orphaned Running child row IN THE OPENING WORKSPACE (a record the new
      // container cannot own). Same-workspace orphans are still repaired.
      AppDatabase seed = new(dbPath);
      SqliteAgentStore seedStore = new(seed);
      AgentRecord orphan = AgentRecord.Spawned(AgentId.NewId(), null, 1, "m/sub", "orphan",
          "task", DateTimeOffset.UtcNow) with
      { WorkspaceId = ws };
      _ = await seedStore.SaveAsync(orphan, TestContext.Current.CancellationToken).ConfigureAwait(true);

      // A foreign-workspace Running row (another session's live agent) must survive.
      AgentRecord foreign = AgentRecord.Spawned(AgentId.NewId(), null, 1, "m/sub", "live-elsewhere",
          "task", DateTimeOffset.UtcNow) with
      { WorkspaceId = "C:\\other-workspace" };
      _ = await seedStore.SaveAsync(foreign, TestContext.Current.CancellationToken).ConfigureAwait(true);

      AgentSessionFactory factory = new(Settings(), new AppDatabase(dbPath));
      Result<AgentSession> session = await factory.CreateAsync(ws, Providers.OpenRouter,
          ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(session.IsSuccess);

      SqliteAgentStore verify = new(new AppDatabase(dbPath));
      Result<AgentRecord> orphanAfter = await verify.GetAsync(orphan.Id, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(orphanAfter.IsSuccess);
      Assert.Equal(AgentStatus.Failed, orphanAfter.Value.Status);
      Assert.Equal(AgentFailureReason.Interrupted, orphanAfter.Value.FailureReason);

      Result<AgentRecord> foreignAfter = await verify.GetAsync(foreign.Id, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(foreignAfter.IsSuccess);
      Assert.Equal(AgentStatus.Running, foreignAfter.Value.Status);

      await session.Value.Services.DisposeAsync().ConfigureAwait(true);
    }
    finally
    {
      Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", null);
      try
      {
        File.Delete(dbPath);
      }
#pragma warning disable CA1031 // Do not catch general exception types
      catch
      {
        // Best-effort temp cleanup is deliberate here (CA1031/S108): the assertion has
        // already run; a locked temp file must not fail the test run.
      }
#pragma warning restore CA1031
    }
  }
}
