using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>W2 wiring: the composed session's AgentLinkRegistry is store-backed — a link
///     consented through the session registry lands in the shared app database (visible to a
///     store over a SECOND AppDatabase instance, i.e. the next session's path), scoped to the
///     session's workspace.</summary>
public class LinkPersistenceWiringTests
{
  private static AgentSettings Settings() => new(
      new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
      new ZaiSettings(null, new Uri("https://zai.test")),
      new SubAgentOptions(null, 2));

  private sealed class SilentChannel : IClarifyChannel
  {
    public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default)
        => Task.FromResult(Result.Success("1"));
  }

  [Fact]
  public async Task Session_Registry_Writes_Through_To_The_Shared_Database()
  {
    string dbPath = Path.Combine(Path.GetTempPath(), "ethang-linkwiring-" + Guid.NewGuid().ToString("N") + ".db");
    string workspaceRoot = Directory.CreateTempSubdirectory("ethang-ws").FullName;
    Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", dbPath);
    try
    {
      AgentSessionFactory factory = new(Settings(), new AppDatabase(dbPath));
      Result<AgentSession> session = await factory.CreateAsync(workspaceRoot, Providers.OpenRouter,
          new SilentChannel(), ct: TestContext.Current.CancellationToken);
      Assert.True(session.IsSuccess);

      AgentLinkRegistry registry = session.Value.Services.GetRequiredService<AgentLinkRegistry>();
      Guid target = Guid.NewGuid();
      Result<LinkAddress> linked = registry.Link("researcher", "container-a", target.ToString("D"), consented: true);
      Assert.True(linked.IsSuccess);

      // A SECOND AppDatabase instance over the same file — the next session's path —
      // sees the consented row: the write went through to the shared database.
      SqliteLinkStore verify = new(new AppDatabase(dbPath));
      StoredLink row = Assert.Single(verify.List(workspaceRoot).Value!);
      Assert.Equal("researcher", row.Name);
      Assert.Equal(target.ToString("D"), row.AgentAddress);

      await session.Value.Services.DisposeAsync().ConfigureAwait(true);
    }
    finally
    {
      Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", null);
      // Named decision (CA1031): temp cleanup is best effort.
#pragma warning disable CA1031, S108 // Do not catch general exception types
      try
      {
        File.Delete(dbPath);
      }
      catch
      {
      }

      try
      {
        Directory.Delete(workspaceRoot, true);
      }
      catch
      {
      }
#pragma warning restore CA1031, S108
    }
  }
}
