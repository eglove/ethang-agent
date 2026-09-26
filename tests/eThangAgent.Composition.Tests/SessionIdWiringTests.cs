using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

// Best-effort temp-file cleanup in finally blocks is deliberate (CA1031).
#pragma warning disable CA1031, S108 // Do not catch general exception types

namespace eThangAgent.Composition.Tests;

/// <summary>The persisted root session id keys OpenRouter sticky sessions: the
///     container's RootAgentHolder resolves it lazily from RootSessionIdentity and
///     stamps it onto every provider request the root agent builds.</summary>
[Collection("EnvironmentSensitive")]
public class SessionIdWiringTests
{
  private static (AgentSessionFactory Factory, string DbPath) CreateFactory()
  {
    string dbPath = Path.Combine(Path.GetTempPath(), $"ethang-sid-{Guid.NewGuid():N}.db");
    Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", dbPath);
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new SubAgentOptions(null, 2));
    return (new AgentSessionFactory(settings), dbPath);
  }

  [Fact]
  public async Task CreatedSession_HolderStamps_PersistedRootId()
  {
    (AgentSessionFactory? factory, string? db) = CreateFactory();
    try
    {
      DirectoryInfo dir = Directory.CreateTempSubdirectory("ethang-sid-ws");
      try
      {
        Result<AgentSession> created = await factory.CreateAsync(dir.FullName, Providers.OpenRouter, ct: TestContext.Current.CancellationToken);
        Assert.True(created.IsSuccess);

        // The persisted root id: the session's own identity.
        AgentId rootId = created.Value.RootId;

        // The container's provider is the real OpenRouter one; assert the wiring
        // instead: the holder's built agent carries the root id as its session id.
        RootAgentHolder holder = created.Value.Services.GetRequiredService<RootAgentHolder>();
        ModelConfig config = ModelConfig.Create("test/model", null, 64, 0.5f, 4096).Value!;
        _ = holder.Build(existing: null, config);

        Assert.Equal(rootId.ToString(), holder.CurrentSessionId);
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
