
// Best-effort temp-file cleanup in catch blocks is deliberate (CA1031), matching
// the sibling session-factory test files.
#pragma warning disable CA1031 // Do not catch general exception types
using eThangAgent.AgentDomain;
using Microsoft.Extensions.DependencyInjection;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
namespace eThangAgent.Composition.Tests;

/// <summary>Factory wiring (spec #26): the session factory resolves the watcher
/// singleton and starts it on create and resume; a container without skill
/// directories still gets the watcher (inert - it watches nothing).</summary>
public class SkillDirectoryWatcherWiringTests
{
  [Fact]
  public async Task CreateAsync_StartsTheWatcher()
  {
    (AgentSessionFactory factory, string db) = CreateFactory();
    try
    {
      DirectoryInfo dir = Directory.CreateTempSubdirectory("ethang-ws-watcher");
      try
      {
        Result<AgentSession> result = await factory.CreateAsync(dir.FullName, Providers.OpenRouter,
            ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.True(result.IsSuccess);
        SkillDirectoryWatcher watcher = result.Value.Services.GetRequiredService<SkillDirectoryWatcher>();
        Assert.True(watcher.IsStarted);
      }
      finally
      {
        dir.Delete(true);
      }
    }
    finally
    {
      DeleteDb(db);
    }
  }

  private static AgentSettings Settings() => new(
      new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
      new ZaiSettings(null, new Uri("https://zai.test")),
      new SubAgentOptions(null, 2));

  private static (AgentSessionFactory Factory, string DbPath) CreateFactory()
  {
    string dbPath = Path.Combine(Path.GetTempPath(), $"ethang-watcher-{Guid.NewGuid():N}.db");
    return (new AgentSessionFactory(Settings(), new AppDatabase(dbPath)), dbPath);
  }

  private static void DeleteDb(string dbPath)
  {
    try
    {
      File.Delete(dbPath);
    }
    catch
    {
      // best effort
    }
  }
}
