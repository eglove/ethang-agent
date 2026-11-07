using eThangAgent.AgentDomain;
using eThangAgent.Lsp.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.TestSupport;
using Microsoft.Extensions.DependencyInjection;

// Best-effort temp-file cleanup in catch blocks is deliberate (CA1031), matching
// the sibling session-factory test files.
#pragma warning disable CA1031 // Do not catch general exception types

namespace eThangAgent.Composition.Tests;

/// <summary>Factory wiring (spec #26): the session factory resolves the watcher
/// singleton and starts it on create and resume; a container without skill
/// directories still gets the watcher (inert - it watches nothing).</summary>
[Trait("Category", "Process")]
public class SkillDirectoryWatcherWiringTests
{
  [Fact]
  public async Task CreateAsync_StartsTheWatcher()
  {
    (AgentSessionFactory factory, string db) = CreateFactory();
    try
    {
      DirectoryInfo dir = TestTempRoot.CreateDirectory("ethang-ws-watcher");
      try
      {
        Result<AgentSession> result = await factory.CreateAsync(dir.FullName, Providers.OpenRouter,
            ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.True(result.IsSuccess);
        try
        {
          SkillDirectoryWatcher watcher = result.Value.Services.GetRequiredService<SkillDirectoryWatcher>();
          Assert.True(watcher.IsStarted);
        }
        finally
        {
          // The session OWNS a host process: dispose the container so the host is
          // killed instead of lingering as a stray.
          await TestSession.DisposeAsync(result);
        }
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

  [Fact]
  public async Task CreateAsync_StartsTheLanguageServer()
  {
    (AgentSessionFactory factory, string db) = CreateFactory();
    try
    {
      DirectoryInfo dir = TestTempRoot.CreateDirectory("ethang-ws-lsp");
      try
      {
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "a.cs"), "public class A { }",
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        Result<AgentSession> result = await factory.CreateAsync(dir.FullName, Providers.OpenRouter,
            ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.True(result.IsSuccess);
        try
        {
          LspDaemonHost daemon = result.Value.Services.GetRequiredService<LspDaemonHost>();
          Assert.True(daemon.IsStarted,
              "the language server must be started at workspace start; an unstarted daemon "
              + "means the first query pays the full daemon + server start.");
        }
        finally
        {
          await TestSession.DisposeAsync(result);
        }
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
      new AgentRunOptions(null, 2));

  private static (AgentSessionFactory Factory, string DbPath) CreateFactory()
  {
    string dbPath = Path.Combine(TestTempRoot.Root, $"ethang-watcher-{Guid.NewGuid():N}.db");
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
