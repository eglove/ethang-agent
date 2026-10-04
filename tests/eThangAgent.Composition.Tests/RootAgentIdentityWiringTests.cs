using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using Microsoft.Extensions.DependencyInjection;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

// Best-effort temp-file cleanup in finally blocks is deliberate (CA1031).
#pragma warning disable CA1031, S108 // Do not catch general exception types

namespace eThangAgent.Composition.Tests;

/// <summary>Phantom-parent regression (2026-10-04): every child row pointed at a
///     parent id that had no row, because the holder built the root agent without an
///     explicit Id. The built root agent's identity must BE the persisted root id.</summary>
public class RootAgentIdentityWiringTests
{
  private static (AgentSessionFactory Factory, string DbPath) CreateFactory()
  {
    string dbPath = Path.Combine(Path.GetTempPath(), $"ethang-rootid-{Guid.NewGuid():N}.db");
    Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", dbPath);
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new SubAgentOptions(null, 2));
    return (new AgentSessionFactory(settings), dbPath);
  }

  [Fact]
  public async Task BuiltRootAgent_Carries_PersistedRootId_AsItsOwnId()
  {
    (AgentSessionFactory? factory, string? db) = CreateFactory();
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
      Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", null);
      try
      {
        File.Delete(db);
      }
      catch { }
    }
  }
}
