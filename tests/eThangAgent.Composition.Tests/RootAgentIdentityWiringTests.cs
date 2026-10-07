using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

// Best-effort temp-file cleanup in finally blocks is deliberate (CA1031).
#pragma warning disable CA1031, S108 // Do not catch general exception types

namespace eThangAgent.Composition.Tests;

/// <summary>Phantom-parent regression (2026-10-04): every child row pointed at a
///     parent id that had no row, because the holder built the root agent without an
///     explicit Id. The built root agent's identity must BE the persisted root id.</summary>
public class RootAgentIdentityWiringTests
{
  private static (AgentSessionFactory Factory, TestAppDatabase Db) CreateFactory()
  {
    // The factory gets the database EXPLICITLY - the env-var fallback races other
    // tests' env clears mid-run, and no test may open the user's real database.
    TestAppDatabase db = TestAppDatabase.Create();
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new SubAgentOptions(null, 2));
    return (new AgentSessionFactory(settings, db.Database), db);
  }

  [Fact]
  public async Task BuiltRootAgent_Carries_PersistedRootId_AsItsOwnId()
  {
    (AgentSessionFactory? factory, TestAppDatabase? db) = CreateFactory();
    try
    {
      DirectoryInfo dir = Directory.CreateTempSubdirectory("ethang-rootid-ws");
      try
      {
        Result<AgentSession> created = await factory.CreateAsync(dir.FullName, Providers.OpenRouter, ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.True(created.IsSuccess);
        AgentId rootId = created.Value.RootId;

        RootAgentHolder holder = created.Value.Services.GetRequiredService<RootAgentHolder>();
        ModelConfig config = ModelConfig.Create("test/model", null, 64, 0.5f, 4096).Value!;
        AgentDomain.Agent agent = holder.Build(existing: null, config);

        Assert.Equal(rootId, agent.Id);
      }
      finally
      {
        dir.Delete(true);
      }
    }
    finally
    {
      db?.Dispose();
    }
  }
}
