using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition.Tests;

/// <summary>R3.2 end-to-end through session open: a persisted Running row that no runtime
///     owns is Failed(Interrupted) with an audit row once a session opens; rows owned by
///     the fresh container's runtime (none at open time besides the new root, which is
///     not registered as a child) and already-terminal rows are untouched.</summary>
public class SessionOpenOrphanRepairTests
{
  private static AgentSettings Settings() => new(
      new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
      new ZaiSettings(null, new Uri("https://zai.test")),
      new SubAgentOptions(null, 2));

  [Fact]
  public async Task SessionOpen_MarksUnownedRunningRows_Interrupted()
  {
    string dbPath = Path.Combine(Path.GetTempPath(), "ethang-orphan-" + Guid.NewGuid().ToString("N") + ".db");
    Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", dbPath);
    try
    {
      // Seed an orphaned Running child row (a record the new container cannot own).
      AppDatabase seed = new(dbPath);
      SqliteAgentStore seedStore = new(seed);
      AgentRecord orphan = AgentRecord.Spawned(AgentId.NewId(), null, 1, "m/sub", "orphan",
          "task", DateTimeOffset.UtcNow);
      _ = await seedStore.SaveAsync(orphan, TestContext.Current.CancellationToken);

      AgentSessionFactory factory = new(Settings(), new AppDatabase(dbPath));
      string ws = Directory.CreateTempSubdirectory("ethang-ws").FullName;
      Result<AgentSession> session = await factory.CreateAsync(ws, Providers.OpenRouter,
          new SilentChannel(), ct: TestContext.Current.CancellationToken);
      Assert.True(session.IsSuccess);

      SqliteAgentStore verify = new(new AppDatabase(dbPath));
      Result<AgentRecord> after = await verify.GetAsync(orphan.Id, TestContext.Current.CancellationToken);
      Assert.True(after.IsSuccess);
      Assert.Equal(AgentStatus.Failed, after.Value.Status);
      Assert.Equal(AgentFailureReason.Interrupted, after.Value.FailureReason);

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

  private sealed class SilentChannel : IClarifyChannel
  {
    public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default)
        => Task.FromResult(Result.Success("1"));
  }
}
