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
[Collection("EnvironmentSensitive")]
public class AgentSessionFactoryTests
{

  private static readonly Uri BaseUrl = new("https://openrouter.test");

  private static AgentSettings Settings(string? openRouterKey = "sk-or-test", string? zaiKey = null) => new(
      new OpenRouterSettings(openRouterKey, BaseUrl),
      new ZaiSettings(zaiKey, new Uri("https://zai.test")),
      new SubAgentOptions(null, 2));

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
        Result<AgentSession> a = await factory.CreateAsync(dirA.FullName, Providers.OpenRouter, ct: TestContext.Current.CancellationToken);
        Result<AgentSession> b = await factory.CreateAsync(dirB.FullName, Providers.OpenRouter, ct: TestContext.Current.CancellationToken);

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
      Result<AgentSession> result = await factory.CreateAsync(missing, Providers.OpenRouter, ct: TestContext.Current.CancellationToken);

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
        Result<AgentSession> result = await factory.CreateAsync(dir.FullName, "anthropic", ct: TestContext.Current.CancellationToken);

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
        Result<AgentSession> result = await factory.CreateAsync(dir.FullName, Providers.Zai, ct: TestContext.Current.CancellationToken);

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
        Assert.False((await factory.CreateAsync(dir.FullName, Providers.Zai, ct: TestContext.Current.CancellationToken)).IsSuccess);

        AgentSessionFactory rebound = factory.WithSettings(Settings(zaiKey: "zai-test-key"));
        Result<AgentSession> opened = await rebound.CreateAsync(dir.FullName, Providers.Zai, ct: TestContext.Current.CancellationToken);
        Assert.True(opened.IsSuccess);
        Assert.Equal(Providers.Zai, opened.Value.ProviderName);

        // The original factory keeps refusing — rebind is a new instance, not a mutation.
        Assert.False((await factory.CreateAsync(dir.FullName, Providers.Zai, ct: TestContext.Current.CancellationToken)).IsSuccess);
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

  /// <summary>Test (e), the anchor resolution path end to end over ONE composed container:
  ///     the real start command's anchor rule refuses an outside anchor (AnchorInvalid,
  ///     before any model resolution); a persisted anchored child record is what the
  ///     handler writes when the rule passes; lifting the container's scope to that
  ///     anchor — the ambient the real spawner creates around a child loop — makes the
  ///     REAL exec engine resolve Workspace at the anchor (its delegate consults the
  ///     scope first), and the spawner's anchored registry view passes the registry's
  ///     unscoped tools through untouched; the scope restores to null after.</summary>
  [Fact]
  public async Task CreateAsync_AnchoredChild_ExecResolvesWorkspaceAtAnchor()
  {
    (AgentSessionFactory? factory, string? db) = CreateFactory();
    try
    {
      DirectoryInfo dir = Directory.CreateTempSubdirectory("ethang-ws-anchor");
      try
      {
        Result<AgentSession> session = await factory.CreateAsync(dir.FullName, Providers.OpenRouter, ct: TestContext.Current.CancellationToken);
        Assert.True(session.IsSuccess);
        ServiceProvider services = session.Value.Services;

        // Composition: the scope exists once per container and starts empty.
        IWorkspaceAnchorScope scope = services.GetRequiredService<IWorkspaceAnchorScope>();
        Assert.Null(scope.Current);
        _ = Assert.IsType<SessionWorkspaceAnchorScope>(scope);

        // The anchor lives INSIDE the session workspace; the probe exists NOWHERE else,
        // so any observation that finds it proves resolution rooted at the anchor.
        string workspaceRoot = services.GetRequiredService<IWorkspaceContext>().WorkspaceId;
        string anchorPath = Path.GetFullPath(Path.Combine(workspaceRoot, "anchored"));
        _ = Directory.CreateDirectory(anchorPath);

        // The spawn-side seam over the real container: an anchored request that
        // resolves OUTSIDE the session workspace fails AnchorInvalid before any model
        // resolution runs (the anchor rule sits ahead of it in the handler).
        string outsidePath = Directory.CreateTempSubdirectory("ethang-anchor-out").FullName;
        try
        {
          Result<AgentId> refused = await services.GetRequiredService<IAgentSpawnCommand>().Execute(
              RootRecord(workspaceRoot),
              new SpawnRequest("nope", WorkspaceRoot: outsidePath),
              ct: TestContext.Current.CancellationToken);
          Assert.False(refused.IsSuccess, $"expected AnchorInvalid, got success");
          Assert.Equal("AnchorInvalid", refused.Error.Code);
        }
        finally
        {
          Directory.Delete(outsidePath, true);
        }

        // The run-side seam: a persisted anchored child (exactly what the start command
        // writes when its anchor rule passes) runs through the container's REAL spawner.
        ModelConfig childModel = ModelConfig.Create("test/model", null, 4096, 0.7f, 32 * 1024).Value!;
        AgentRecord child = AgentRecord.Spawned(AgentId.NewId(), RootRecord(workspaceRoot).Id, 1,
            childModel.ModelId, "anchored", "resolve the anchor", DateTimeOffset.UtcNow,
            new SpawnContract(WorkspaceRoot: anchorPath));
        _ = await services.GetRequiredService<IAgentStore>().SaveAsync(child, TestContext.Current.CancellationToken);

        // The child run's ambient, reproduced exactly as the spawner creates it: the
        // scope lifted to the anchor for the duration of the observation. The exec
        // engine — the REAL composed engine whose workspace delegate consults the
        // scope FIRST — must then resolve Workspace at the anchor.
        scope.Current = anchorPath;
        try
        {
          ExecRunResult exec = await services.GetRequiredService<IExecEngine>()
              .ExecuteAsync(new ExecProgram("return Workspace;"), ct: TestContext.Current.CancellationToken);
          Assert.True(exec.Output.Contains(anchorPath, StringComparison.OrdinalIgnoreCase),
              $"expected Workspace at the anchor, got: {exec.Output}");

          // The spawner's anchored view over the container registry: unscoped tools
          // (exec IS the registry's only tool in this composition) pass through
          // unchanged, so the child loop keeps its surface; definitions delegate.
          AnchoredToolRegistry anchored = new(
              services.GetRequiredService<IToolRegistry>(), anchorPath);
          ITool execTool = anchored.Find(ExecTool.ToolName)
              ?? throw new InvalidOperationException("the anchored view lost 'exec'.");
          Assert.NotNull(execTool.Definition);
        }
        finally
        {
          scope.Current = null;
        }

        // The scope was restored after the run.
        Assert.Null(scope.Current);

        // The spawned child row: the run is never started here (no provider is wired
        // for it), so mark the row Interrupted — a settled row, never a phantom Running.
        _ = await services.GetRequiredService<IAgentStore>().UpdateAsync(child with
        {
          Status = AgentStatus.Interrupted,
          CompletedAt = DateTimeOffset.UtcNow,
        }, TestContext.Current.CancellationToken);
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

  /// <summary>A depth-0 root record for driving the spawn handler directly: bound to the
  ///     workspace so the anchor rule measures the request against it.</summary>
  private static AgentRecord RootRecord(string workspaceRoot) => new(
      new AgentId(Guid.NewGuid()), null, 0, AgentStatus.Running, null,
      "test/model", "root", "root task", DateTimeOffset.UtcNow, null, null,
      workspaceRoot, Providers.OpenRouter);

  [Fact]
  public async Task CreateAsync_ZaiConfigured_WiresZaiProviderAndCarriesProviderName()
  {
    (AgentSessionFactory? factory, string? db) = CreateFactory(Settings(zaiKey: "zai-test-key"));
    try
    {
      DirectoryInfo dir = Directory.CreateTempSubdirectory("ethang-ws-zc");
      try
      {
        Result<AgentSession> result = await factory.CreateAsync(dir.FullName, Providers.Zai, ct: TestContext.Current.CancellationToken);

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

  [Fact]
  public async Task CreateAsync_Session_Carries_The_Rendered_System_Prompt()
  {
    (AgentSessionFactory? factory, string? db) = CreateFactory();
    try
    {
      DirectoryInfo dir = Directory.CreateTempSubdirectory("ethang-ws-sp");
      try
      {
        Result<AgentSession> result = await factory.CreateAsync(dir.FullName, Providers.OpenRouter, ct: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        string prompt = result.Value.SystemPrompt;
        Assert.False(string.IsNullOrWhiteSpace(prompt));
        // The composite render: the skills bootstrap leads, session files follow.
        Assert.Contains("EXTREMELY_IMPORTANT", prompt, StringComparison.Ordinal);
        Assert.Contains("You are eThang Agent", prompt, StringComparison.Ordinal);
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
