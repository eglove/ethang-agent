using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;
using eThangAgent.ToolDomain;
using eThangAgent.Zai.ACL;
using Microsoft.Extensions.DependencyInjection;

// Best-effort temp-file cleanup in finally blocks is deliberate (CA1031).
#pragma warning disable CA1031, S108 // Do not catch general exception types

namespace eThangAgent.Composition.Tests;

/// <summary>The session factory is the multi-workspace seam: each created session
///     must carry its own workspace identity and path resolver rooted at the chosen
///     directory while sharing the one app database. Sessions are also the provider
///     seam: each is wired exclusively for the provider it was opened with.</summary>
public class AgentSessionFactoryTests
{
  private sealed class StubChannel : IClarifyChannel
  {
    public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default)
        => Task.FromResult(Result.Success("1"));
  }

  private static readonly Uri BaseUrl = new("https://openrouter.test");

  private static AgentSettings Settings(string? openRouterKey = "sk-or-test", string? zaiKey = null) => new(
      new OpenRouterSettings(openRouterKey, BaseUrl),
      new ZaiSettings(zaiKey, new Uri("https://zai.test")),
      new SubAgentOptions(null, TimeSpan.FromSeconds(300), 2));

  private static (AgentSessionFactory Factory, string DbPath) CreateFactory(AgentSettings? settings = null)
  {
    string dbPath = Path.Combine(Path.GetTempPath(), $"ethang-factory-{Guid.NewGuid():N}.db");
    Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", dbPath);
    return (new AgentSessionFactory(settings ?? Settings()), dbPath);
  }

  [Fact]
  public async Task CreateAsync_Builds_Isolated_Sessions_Per_Workspace()
  {
    (AgentSessionFactory? factory, string? db) = CreateFactory();
    try
    {
      DirectoryInfo dirA = Directory.CreateTempSubdirectory("ethang-ws-a");
      DirectoryInfo dirB = Directory.CreateTempSubdirectory("ethang-ws-b");
      try
      {
        Result<AgentSession> a = await factory.CreateAsync(dirA.FullName, Providers.OpenRouter, new StubChannel(), ct: TestContext.Current.CancellationToken);
        Result<AgentSession> b = await factory.CreateAsync(dirB.FullName, Providers.OpenRouter, new StubChannel(), ct: TestContext.Current.CancellationToken);

        Assert.True(a.IsSuccess);
        Assert.True(b.IsSuccess);

        // Distinct roots, identities, resolvers, conversations — nothing shared.
        Assert.NotEqual(a.Value.RootId, b.Value.RootId);
        Assert.NotSame(a.Value.Conversation, b.Value.Conversation);
        Assert.Equal(dirA.FullName, a.Value.WorkspaceRoot);
        Assert.Equal(dirB.FullName, b.Value.WorkspaceRoot);

        // Each session's workspace context carries ITS OWN root path as identity.
        IWorkspaceContext ctxA = a.Value.Services.GetRequiredService<IWorkspaceContext>();
        IWorkspaceContext ctxB = b.Value.Services.GetRequiredService<IWorkspaceContext>();
        Assert.Equal(dirA.FullName, ctxA.WorkspaceId);
        Assert.Equal(dirB.FullName, ctxB.WorkspaceId);

        // Path resolution is jailed to each session's own root.
        IPathResolver resolver = a.Value.Services.GetRequiredService<IPathResolver>();
        Assert.True(resolver.Resolve("file.txt").IsSuccess);
        Assert.False(
            resolver.Resolve(Path.Combine(dirB.FullName, "escape.txt")).IsSuccess);

        // The exec engine resolves Workspace per execution against the session's
        // own identity — never a process-global cwd captured at construction.
        IExecEngine engine = a.Value.Services.GetRequiredService<IExecEngine>();
        ExecRunResult run = await engine.ExecuteAsync(new ExecProgram("return Workspace;"), ct: TestContext.Current.CancellationToken);
        Assert.Contains(dirA.FullName, run.Output, StringComparison.OrdinalIgnoreCase);
      }
      finally
      {
        dirA.Delete(true);
        dirB.Delete(true);
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

  [Fact]
  public async Task CreateAsync_Rejects_Missing_Directory_With_Structured_Error()
  {
    (AgentSessionFactory? factory, string? db) = CreateFactory();
    try
    {
      string missing = Path.Combine(Path.GetTempPath(), $"ethang-missing-{Guid.NewGuid():N}");
      Result<AgentSession> result = await factory.CreateAsync(missing, Providers.OpenRouter, new StubChannel(), ct: TestContext.Current.CancellationToken);

      Assert.False(result.IsSuccess);
      Assert.Equal("WorkspaceNotFound", result.Error.Code);
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

  [Fact]
  public async Task CreateAsync_Rejects_Unknown_Provider_With_Structured_Error()
  {
    (AgentSessionFactory? factory, string? db) = CreateFactory();
    try
    {
      DirectoryInfo dir = Directory.CreateTempSubdirectory("ethang-ws-u");
      try
      {
        Result<AgentSession> result = await factory.CreateAsync(dir.FullName, "anthropic", new StubChannel(), ct: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("UnknownProvider", result.Error.Code);
        Assert.Contains("anthropic", result.Error.Message, StringComparison.Ordinal);
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

  [Fact]
  public async Task CreateAsync_Rejects_Unconfigured_Provider_With_Structured_Error()
  {
    // OpenRouter key present, z.ai key absent: opening a z.ai session must fail
    // with a structured error naming the provider.
    (AgentSessionFactory? factory, string? db) = CreateFactory();
    try
    {
      DirectoryInfo dir = Directory.CreateTempSubdirectory("ethang-ws-z");
      try
      {
        Result<AgentSession> result = await factory.CreateAsync(dir.FullName, Providers.Zai, new StubChannel(), ct: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("ProviderNotConfigured", result.Error.Code);
        Assert.Contains("z.ai", result.Error.Message, StringComparison.Ordinal);
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

  [Fact]
  public async Task WithSettings_Serves_The_Updated_Keys_On_Future_Sessions()
  {
    // The rebind seam behind the settings modal: a factory built without a z.ai
    // key refuses z.ai sessions; after WithSettings with the key, future sessions
    // open — over the SAME app database — without touching the original factory.
    (AgentSessionFactory factory, string db) = CreateFactory();
    try
    {
      DirectoryInfo dir = Directory.CreateTempSubdirectory("ethang-ws-rebind");
      try
      {
        Assert.False((await factory.CreateAsync(dir.FullName, Providers.Zai, new StubChannel(), ct: TestContext.Current.CancellationToken)).IsSuccess);

        AgentSessionFactory rebound = factory.WithSettings(Settings(zaiKey: "zai-test-key"));
        Result<AgentSession> opened = await rebound.CreateAsync(dir.FullName, Providers.Zai, new StubChannel(), ct: TestContext.Current.CancellationToken);
        Assert.True(opened.IsSuccess);
        Assert.Equal(Providers.Zai, opened.Value.ProviderName);

        // The original factory keeps refusing — rebind is a new instance, not a mutation.
        Assert.False((await factory.CreateAsync(dir.FullName, Providers.Zai, new StubChannel(), ct: TestContext.Current.CancellationToken)).IsSuccess);
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

  [Fact]
  public void WithSettings_Null_Throws()
  {
    (AgentSessionFactory factory, string db) = CreateFactory();
    try
    {
      _ = Assert.Throws<ArgumentNullException>(() => factory.WithSettings(null!));
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

  [Fact]
  public async Task CreateAsync_ZaiConfigured_WiresZaiProviderAndCarriesProviderName()
  {
    (AgentSessionFactory? factory, string? db) = CreateFactory(Settings(zaiKey: "zai-test-key"));
    try
    {
      DirectoryInfo dir = Directory.CreateTempSubdirectory("ethang-ws-zc");
      try
      {
        Result<AgentSession> result = await factory.CreateAsync(dir.FullName, Providers.Zai, new StubChannel(), ct: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(Providers.Zai, result.Value.ProviderName);
        _ = Assert.IsType<ZaiModelCatalog>(result.Value.Services.GetRequiredService<IModelCatalog>());
        _ = Assert.IsType<ZaiModelProvider>(result.Value.Services.GetRequiredService<IModelProvider>());
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
