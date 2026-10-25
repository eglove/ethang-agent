using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>W2 wiring: the composed session's AgentLinkRegistry is store-backed - a link
///     consented through the session registry lands in the shared app database (visible to a
///     store over a SECOND AppDatabase instance, i.e. the next session's path), scoped to the
///     session's workspace.
///     <para>Derives from <see cref="SessionOwningTest"/> (issue #284 item 3): disposal as the
///     last statement of the test body was skipped whenever an assertion threw above it,
///     stranding a ChildHost that holds its SQLite file.</para></summary>
[Collection("EnvironmentSensitive")]
public class LinkPersistenceWiringTests : SessionOwningTest
{
  private static AgentSettings Settings() => new(
      new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
      new SubAgentOptions(null, 2));

  [Fact]
  public async Task Session_Registry_Writes_Through_To_The_Shared_Database()
  {
    AgentSettings settings = Settings();
    AgentSession session = await CreateSessionAsync(settings);

    AgentLinkRegistry registry = session.Services.GetRequiredService<AgentLinkRegistry>();
    Guid target = Guid.NewGuid();
    Result<LinkAddress> linked = registry.Link("researcher", "container-a", target.ToString("D"), consented: true);
    Assert.True(linked.IsSuccess);

    // A SECOND AppDatabase instance over the same file - the next session's path - sees
    // the consented row: the write went through to the shared database.
    string dbPath = Environment.GetEnvironmentVariable("ETHANG_AGENT_DB")!;
    SqliteLinkStore verify = new(new AppDatabase(dbPath));
    StoredLink row = Assert.Single(verify.List(session.WorkspaceRoot).Value!);
    Assert.Equal("researcher", row.Name);
    Assert.Equal(target.ToString("D"), row.AgentAddress);
  }
}
