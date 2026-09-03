using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using eThangAgent.Zai.ACL;
using Microsoft.Extensions.DependencyInjection;

// Best-effort temp-file cleanup in finally blocks is deliberate (CA1031).
#pragma warning disable CA1031, S108 // Do not catch general exception types

namespace eThangAgent.Composition.Tests;

/// <summary>The session factory's local bootstrap (Task 9): opening (or resuming) a
///     session on the local provider resolves its default model from the server's OWN
///     model list — first listed entry, with its advertised context window — BEFORE any
///     container is built. No pseudo-model exists server-side, so turn one needs a real
///     id. A server that cannot answer fails the open with a structured
///     ProviderUnreachable error and leaves nothing half-built; every other provider
///     keeps the synchronous constant bootstrap byte-identically. Also pins the routed
///     strictness fix: a non-blank but unusable local base URL fails with the named
///     InvalidLocalBaseUrl error, never a raw exception across the Task result seam.</summary>
public class LocalSessionFactoryTests
{
  private sealed class SilentClarifyChannel : IClarifyChannel
  {
    public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default)
        => throw new NotSupportedException("No test should reach the human.");
  }

  private static readonly Uri OpenRouterBaseUrl = new("https://openrouter.test");

  private static AgentSettings Settings(LocalSettings? local, string? zaiKey = null) => new(
      new OpenRouterSettings("sk-or-test", OpenRouterBaseUrl),
      new ZaiSettings(zaiKey, new Uri("https://zai.test")),
      new SubAgentOptions(null, 2),
      Local: local);

  private static LocalSettings LocalAt(string baseUrlText) => new(baseUrlText, ApiKey: null);

  private static (AgentSessionFactory Factory, string DbPath) CreateFactory(AgentSettings settings)
  {
    string dbPath = Path.Combine(Path.GetTempPath(), $"ethang-local-factory-{Guid.NewGuid():N}.db");
    return (new AgentSessionFactory(settings, new AppDatabase(dbPath)), dbPath);
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

  [Fact]
  public async Task CreateAsync_Local_ResolvesFirstModelAsBootstrap()
  {
    using MockLocalServer server = new MockLocalServer()
        .WithModels(/*lang=json,strict*/ """{"data":[{"id":"first-model","context_length":8192}]}""")
        .WithLmStudio(/*lang=json,strict*/ """{"data":[{"id":"first-model","context_length":8192}]}""");
    server.Start();
    (AgentSessionFactory factory, string db) = CreateFactory(Settings(LocalAt(server.BaseUrl.AbsoluteUri)));
    AgentSession? opened = null;
    try
    {
      DirectoryInfo dir = Directory.CreateTempSubdirectory("ethang-ws-local-boot");
      try
      {
        Result<AgentSession> result = await factory.CreateAsync(
            dir.FullName, Providers.Local, new SilentClarifyChannel(),
            ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess);
        opened = result.Value;
        Assert.Equal("first-model", opened.ModelId);

        // The session's bootstrap ModelConfig IS the server's own first entry, with
        // the context window the server advertised for it — turn one can name a real
        // model because the bootstrap was resolved from the lineup before composition.
        ModelConfig config = opened.Services.GetRequiredService<ModelConfig>();
        Assert.Equal("first-model", config.ModelId);
        Assert.Equal(8192, config.ContextWindow);

        // The resolved bootstrap travels as the FALLBACK (Task 8's threading, fed for
        // real): root selection serves the server's own id, never a static default.
        Assert.Equal("first-model", typeof(RootAgentResolver)
            .GetField("_fallbackModelId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(opened.Services.GetRequiredService<RootAgentResolver>()));
      }
      finally
      {
        dir.Delete(true);
      }
    }
    finally
    {
      if (opened is not null)
      {
        await opened.Services.DisposeAsync().ConfigureAwait(true);
      }

      DeleteDb(db);
    }
  }

  [Fact]
  public async Task CreateAsync_Local_ServerDown_Fails_ProviderUnreachable_NothingHalfBuilt()
  {
    // A port with nothing listening: the open must fail with the catalog's structured
    // ProviderUnreachable error — no exception may cross the Task result seam, and no
    // half-built container may survive (the failure fires BEFORE BuildContainer).
    int port = MockLocalServer.FreePort();
    (AgentSessionFactory factory, string db) = CreateFactory(Settings(LocalAt($"http://127.0.0.1:{port}")));
    try
    {
      DirectoryInfo dir = Directory.CreateTempSubdirectory("ethang-ws-local-down");
      try
      {
        Result<AgentSession> result = await factory.CreateAsync(
            dir.FullName, Providers.Local, new SilentClarifyChannel(),
            ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("ProviderUnreachable", result.Error.Code);
        Assert.Contains("local server", result.Error.Message, StringComparison.OrdinalIgnoreCase);
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
  public async Task CreateAsync_Local_InvalidBaseUrlText_Fails_InvalidLocalBaseUrl_NoRawException()
  {
    // The routed strictness fix (Task 8 review): a NON-BLANK but unusable URL text
    // passes the HasLocal gate, so ValidateProvider must resolve it itself and fail
    // with the settings type's own InvalidLocalBaseUrl error — the only provider
    // misconfiguration path that used to throw a raw InvalidOperationException.
    (AgentSessionFactory factory, string db) = CreateFactory(Settings(LocalAt("not a url")));
    try
    {
      DirectoryInfo dir = Directory.CreateTempSubdirectory("ethang-ws-local-badurl");
      try
      {
        Result<AgentSession> result = await factory.CreateAsync(
            dir.FullName, Providers.Local, new SilentClarifyChannel(),
            ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidLocalBaseUrl", result.Error.Code);
        Assert.Contains("not a url", result.Error.Message, StringComparison.Ordinal);
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
  public async Task CreateAsync_ZaiConfigured_StillSucceeds_AsBefore()
  {
    // Regression guard: the local arm's async detour must leave the non-local
    // providers' synchronous constant path byte-identical.
    (AgentSessionFactory factory, string db) = CreateFactory(Settings(local: null, zaiKey: "zai-test-key"));
    AgentSession? opened = null;
    try
    {
      DirectoryInfo dir = Directory.CreateTempSubdirectory("ethang-ws-local-zai");
      try
      {
        Result<AgentSession> result = await factory.CreateAsync(
            dir.FullName, Providers.Zai, new SilentClarifyChannel(),
            ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess);
        opened = result.Value;
        Assert.Equal(Providers.Zai, opened.ProviderName);
        Assert.Equal("glm-5.3-flash", opened.ModelId);
        _ = Assert.IsType<ZaiModelCatalog>(opened.Services.GetRequiredService<IModelCatalog>());
      }
      finally
      {
        dir.Delete(true);
      }
    }
    finally
    {
      if (opened is not null)
      {
        await opened.Services.DisposeAsync().ConfigureAwait(true);
      }

      DeleteDb(db);
    }
  }

  [Fact]
  public async Task ResumeAsync_Local_ResolvesBootstrapModelFromServer()
  {
    // Resume shares the create path's bootstrap: the transcript hydrates, and the
    // default model still comes from the server's own lineup — one shared resolution,
    // not a create-only detour.
    using MockLocalServer server = new MockLocalServer()
        .WithModels(/*lang=json,strict*/ """{"data":[{"id":"first-model","context_length":8192}]}""")
        .WithLmStudio(/*lang=json,strict*/ """{"data":[{"id":"first-model","context_length":8192}]}""");
    server.Start();
    (AgentSessionFactory factory, string db) = CreateFactory(Settings(LocalAt(server.BaseUrl.AbsoluteUri)));
    AgentId rootId;
    try
    {
      DirectoryInfo dir = Directory.CreateTempSubdirectory("ethang-ws-local-resume");
      try
      {
        Result<AgentSession> created = await factory.CreateAsync(
            dir.FullName, Providers.Local, new SilentClarifyChannel(),
            ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.True(created.IsSuccess);
        rootId = created.Value.RootId;

        // One persisted turn slice, then the tab closes (Completed row, container gone).
        SqliteAgentStore store = new(new AppDatabase(db));
        Assert.True((await store.AppendMessageAsync(rootId,
            new Message(Role.User, "local question", DateTimeOffset.UtcNow),
            ct: TestContext.Current.CancellationToken).ConfigureAwait(true)).IsSuccess);
        await created.Value.Lifecycle.CompleteAsync(rootId, _ => Assert.Fail("no complete errors expected"))
            .ConfigureAwait(true);
        await created.Value.Services.DisposeAsync().ConfigureAwait(true);

        Result<AgentSession> resumed = await factory.ResumeAsync(rootId, new SilentClarifyChannel(),
            ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(resumed.IsSuccess);
        AgentSession session = resumed.Value;
        Assert.Equal(Providers.Local, session.ProviderName);
        Assert.Equal("first-model", session.ModelId);
        Assert.Equal(8192, session.Services.GetRequiredService<ModelConfig>().ContextWindow);
        _ = Assert.Single(session.Conversation.Messages);
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
}

/// <summary>Minimal OpenAI-compatible local-server mock — the Desktop E2E
///     MockOpenRouterServer skeleton shrunk to the routes the local provider consumes:
///     /v1/models serves the lineup JSON, /api/v0/models serves the batch
///     context-length JSON the catalog probes (LM Studio tier — the route that carries
///     a context_length; without it the catalog's floor would mask the advertised
///     window), and /chat/completions serves one canned completion. Loopback-only,
///     per-test lifetime.</summary>
internal sealed class MockLocalServer : IDisposable
{
  private readonly HttpListener _listener = new();
  private readonly CancellationTokenSource _cts = new();
  private string _modelsJson = /*lang=json,strict*/ """{"data":[]}""";
  private string _lmStudioJson = /*lang=json,strict*/ """{"data":[]}""";

  public Uri BaseUrl { get; private set; } = null!;

  /// <summary>A free loopback port with nothing listening on it — the server-down fixture.</summary>
  public static int FreePort()
  {
    using TcpListener listener = new(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
  }

  public MockLocalServer WithModels(string modelsJson)
  {
    _modelsJson = modelsJson;
    return this;
  }

  public MockLocalServer WithLmStudio(string lmStudioJson)
  {
    _lmStudioJson = lmStudioJson;
    return this;
  }

  public void Start()
  {
    // devskim: ignore DS162092 - E2E mock provider server must bind to loopback
    BaseUrl = new Uri($"http://127.0.0.1:{FreePort()}/");
    _listener.Prefixes.Add(BaseUrl.AbsoluteUri);
    _listener.Start();
    _ = Task.Run(LoopAsync, _cts.Token);
  }

  private async Task LoopAsync()
  {
    while (!_cts.IsCancellationRequested)
    {
      HttpListenerContext ctx;
      try
      {
        ctx = await _listener.GetContextAsync().ConfigureAwait(false);
      }
      catch (HttpListenerException)
      {
        break;
      }
      catch (ObjectDisposedException)
      {
        break;
      }

      string path = ctx.Request.Url!.AbsolutePath;
      string? body = path switch
      {
        "/v1/models" => _modelsJson,
        "/api/v0/models" => _lmStudioJson,
        "/chat/completions" => /*lang=json,strict*/ """{"choices":[{"message":{"role":"assistant","content":"ok"}}]}""",
        _ => null,
      };
      if (body is null)
      {
        ctx.Response.StatusCode = 404;
      }
      else
      {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
      }

      ctx.Response.Close();
    }
  }

  public void Dispose()
  {
    _cts.Cancel();
    _cts.Dispose();
    _listener.Stop();
    _listener.Close();
  }
}
