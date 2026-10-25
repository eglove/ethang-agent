using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.TestSupport;

namespace eThangAgent.Composition.Tests;

/// <summary>R3.2 end-to-end through session open: a persisted Running row that no runtime
///     owns is Failed(Interrupted) with an audit row once a session opens - but ONLY rows in
///     the OPENING session's workspace (2026-10-04 scope fix: other workspaces' Running rows
///     are other sessions' live state). Rows owned by the fresh container's runtime and
///     already-terminal rows are untouched.
///     <para>Derives from <see cref="SessionOwningTest"/> (issue #284 item 3). It seeds its
///     rows over a database the base class creates, so the workspace is passed explicitly
///     and its path recorded for teardown.</para></summary>
[Collection("EnvironmentSensitive")]
public class SessionOpenOrphanRepairTests : SessionOwningTest
{
  private static AgentSettings Settings() => new(
      new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
      new SubAgentOptions(null, 2));

  [Fact]
  public async Task SessionOpen_MarksUnownedRunningRows_Interrupted()
  {
    string ws = Track(Directory.CreateTempSubdirectory("ethang-ws").FullName);

    // Seed BEFORE the session opens: the orphan must exist when open-time repair runs.
    // The base class below selects the temp database this seeding targets.
    AgentSettings settings = Settings();
    string dbPath = Track(Path.Combine(Path.GetTempPath(),
        "ethang-orphan-" + Guid.NewGuid().ToString("N") + ".db"));
    Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", dbPath);

    // Seed an orphaned Running child row IN THE OPENING WORKSPACE (a record the new
    // container cannot own). Same-workspace orphans are still repaired.
    SqliteAgentStore seedStore = new(new AppDatabase(dbPath));
    AgentRecord orphan = AgentRecord.Spawned(AgentId.NewId(), null, 1, "m/sub", "orphan",
        "task", DateTimeOffset.UtcNow) with
    { WorkspaceId = ws };
    _ = await seedStore.SaveAsync(orphan, TestContext.Current.CancellationToken).ConfigureAwait(true);

    // A foreign-workspace Running row (another session's live agent) must survive.
    AgentRecord foreign = AgentRecord.Spawned(AgentId.NewId(), null, 1, "m/sub", "live-elsewhere",
        "task", DateTimeOffset.UtcNow) with
    { WorkspaceId = "C:\\other-workspace" };
    _ = await seedStore.SaveAsync(foreign, TestContext.Current.CancellationToken).ConfigureAwait(true);

    // Session creation over THIS database and workspace runs open-time repair.
    AgentSessionFactory factory = new(settings, new AppDatabase(dbPath));
    Result<AgentSession> session = await factory.CreateAsync(ws, Providers.OpenRouter,
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(session.IsSuccess);
    TrackSession(session);

    SqliteAgentStore verify = new(new AppDatabase(dbPath));
    Result<AgentRecord> orphanAfter = await verify.GetAsync(orphan.Id, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(orphanAfter.IsSuccess);
    Assert.Equal(AgentStatus.Failed, orphanAfter.Value.Status);
    Assert.Equal(AgentFailureReason.Interrupted, orphanAfter.Value.FailureReason);

    Result<AgentRecord> foreignAfter = await verify.GetAsync(foreign.Id, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(foreignAfter.IsSuccess);
    Assert.Equal(AgentStatus.Running, foreignAfter.Value.Status);
  }
}
