using System.Net;
using System.Net.Sockets;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

// Best-effort temp-file cleanup and listener-loop teardown are deliberate (CA1031).
#pragma warning disable CA1031 // Do not catch general exception types

namespace eThangAgent.Composition.Tests;

/// <summary>App attribution rides EVERY OpenRouter request: the named "OpenRouter"
/// client (children's per-spawn providers, the catalog) AND the typed IModelProvider
/// client (the root agent and the model selector) must both carry the three
/// attribution headers. Field finding: root sessions appeared on OpenRouter's
/// analytics as plain "api" because only the named client had the headers applied —
/// the typed client is a SEPARATE HttpClient instance whose defaults never saw
/// OpenRouterAppAttribution.Apply. Pinned end-to-end over the REAL composition
/// against a local capture server.</summary>
public class ProviderAttributionWiringTests
{
  /// <summary>A local server that captures the headers of the first POST to
  /// /api/v1/responses and answers a minimal valid Responses-API body.</summary>
  private sealed class CapturingServer : IDisposable
  {
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();

    public Uri BaseUrl { get; private set; } = null!;
    public string? Referer { get; private set; }
    public string? Title { get; private set; }
    public string? Categories { get; private set; }

    public void Start()
    {
      using TcpListener probe = new(IPAddress.Loopback, 0);
      probe.Start();
      int port = ((IPEndPoint)probe.LocalEndpoint).Port;
      probe.Stop();
      BaseUrl = new Uri($"http://127.0.0.1:{port}/");
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
        catch (Exception)
        {
          break;
        }

        if (ctx.Request.Url!.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal))
        {
          Referer = ctx.Request.Headers["HTTP-Referer"];
          Title = ctx.Request.Headers["X-OpenRouter-Title"];
          Categories = ctx.Request.Headers["X-OpenRouter-Categories"];
          byte[] body = System.Text.Encoding.UTF8.GetBytes(/*lang=json,strict*/"""{"output":[{"type":"message","content":[{"type":"output_text","text":"hi"}]}],"status":"completed","usage":{"input_tokens":1,"output_tokens":1}}""");
          ctx.Response.StatusCode = 200;
          ctx.Response.ContentType = "application/json";
          ctx.Response.ContentLength64 = body.Length;
          await ctx.Response.OutputStream.WriteAsync(body, _cts.Token).ConfigureAwait(false);
          ctx.Response.Close();
          continue;
        }

        // Catalog pre-flight: answer empty so it never blocks the turn.
        if (ctx.Request.Url.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
        {
          byte[] models = System.Text.Encoding.UTF8.GetBytes(/*lang=json,strict*/"""{"data":[]}""");
          ctx.Response.StatusCode = 200;
          ctx.Response.ContentType = "application/json";
          ctx.Response.ContentLength64 = models.Length;
          await ctx.Response.OutputStream.WriteAsync(models, _cts.Token).ConfigureAwait(false);
          ctx.Response.Close();
          continue;
        }

        ctx.Response.StatusCode = 404;
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

  [Fact]
  public async Task TypedModelProviderClient_CarriesAppAttribution()
  {
    using CapturingServer server = new();
    server.Start();
    string dbPath = Path.Combine(Path.GetTempPath(), $"ethang-attrib-{Guid.NewGuid():N}.db");
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", server.BaseUrl),
        new SubAgentOptions(null, 2));
    try
    {
      using ServiceProvider services = new ServiceCollection()
          .AddEThangAgentCore(settings, Providers.OpenRouter,
              ModelConfig.Create("test/model", null, 512, 0.5f, 8192).Value!,
              new AgentHostOptions(
                  new FixedWorkspaceContext("app"), new UnrootedPathResolver()))
          .BuildServiceProvider();

      IModelProvider provider = services.GetRequiredService<IModelProvider>();

      Result<ModelResponse> result = await provider.SendAsync(
          ModelConfig.Create("test/model", null, 512, 0.5f, 8192).Value!,
          new ModelRequest([new Message(Role.User, "hi", DateTimeOffset.UtcNow)]),
          TestContext.Current.CancellationToken).ConfigureAwait(true);

      Assert.True(result.IsSuccess, result.Error?.Message);
      Assert.Equal("https://github.com/eglove/ethang-agent", server.Referer);
      Assert.Equal("eThang Agent", server.Title);
      Assert.Equal("programming-app", server.Categories);
    }
    finally
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

#pragma warning restore CA1031 // Do not catch general exception types
}
