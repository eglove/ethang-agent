using System.Net;
using System.Net.Sockets;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;

// Best-effort temp-file cleanup in finally blocks is deliberate (CA1031).
#pragma warning disable CA1031 // Do not catch general exception types

namespace eThangAgent.Composition.Tests;

/// <summary>The headless eval runner's unblock contract (field-failure fix): a turn
///     driven with a budget CancellationToken completes even when the provider accepts
///     the connection and NEVER answers. The pre-fix runner passed
///     CancellationToken.None-equivalent (no token), so a wedged provider call hung the
///     whole eval forever. The budget turns the wedge into a per-row failure — the same
///     mechanism RunPromptAsync now drives. Pinned end-to-end over the REAL composition
///     (real container, real OpenRouter wire) against a server that stalls.</summary>
public class HeadlessTurnBudgetTests
{
  /// <summary>A local OpenAI-compatible server that ACCEPTS chat completions and then
  ///     goes silent: headers never arrive, the client read blocks until its token fires.
  ///     The OpenRouter-shaped catalog endpoints answer (the turn's pre-flight crawl
  ///     must resolve fast — the stall under test is the chat completion, and the
  ///     crawl shares the turn's budget token).</summary>
  private sealed class StallingLocalServer : IDisposable
  {
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();

    public Uri BaseUrl { get; private set; } = null!;

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

        if (ctx.Request.Url!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
        {
          byte[] models = /*lang=json,strict*/ """{"data":[{"id":"stall-model","context_length":8192}]}"""u8.ToArray();
          ctx.Response.StatusCode = 200;
          ctx.Response.ContentType = "application/json";
          ctx.Response.ContentLength64 = models.Length;
          await ctx.Response.OutputStream.WriteAsync(models, _cts.Token).ConfigureAwait(false);
          ctx.Response.Close();
          continue;
        }

        // The per-model endpoints crawl: 404 lands the model on its fallback entry
        // (the client's named degrade path) instead of hanging the pre-flight.
        if (ctx.Request.Url.AbsolutePath.EndsWith("/endpoints", StringComparison.Ordinal))
        {
          ctx.Response.StatusCode = 404;
          ctx.Response.Close();
          continue;
        }

        // Chat completions: hold the request open, answer nothing, until disposed.
        _ = Task.Delay(Timeout.Infinite, _cts.Token);
        try
        {
          await ctx.Response.OutputStream.FlushAsync(_cts.Token).ConfigureAwait(false);
        }
        catch
        {
          // the client's cancellation tears the socket down - expected
        }
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
  public async Task Handle_WithBudgetCancellationToken_FailsWhenProviderStalls()
  {
    using StallingLocalServer server = new();
    server.Start();
    string dbPath = Path.Combine(Path.GetTempPath(), $"ethang-budget-{Guid.NewGuid():N}.db");
    DirectoryInfo workspace = Directory.CreateTempSubdirectory("ethang-budget-ws");
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", server.BaseUrl),
        new SubAgentOptions(null, 2));
    AgentSessionFactory factory = new(settings, new AppDatabase(dbPath));
    try
    {
      Result<AgentSession> created = await factory.CreateAsync(
          workspace.FullName, Providers.OpenRouter, ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(created.IsSuccess, created.Error?.Message);
      AgentSession session = created.Value;

      // THE PIN: the turn must surface within the budget, not hang forever. A generous
      // margin over the wire's own timeouts keeps this deterministic on loaded CI.
      using CancellationTokenSource budget = new(TimeSpan.FromSeconds(35));
      Result<string> result = await session.Handler.Handle(
          new SendMessageCommand("say hello"), ct: budget.Token).ConfigureAwait(true);

      Assert.False(result.IsSuccess, "a stalled provider must fail the turn, never hang it");
      await session.Services.DisposeAsync().ConfigureAwait(true);
    }
    finally
    {
      try
      {
        File.Delete(dbPath);
        workspace.Delete(recursive: true);
      }
      catch
      {
        // best effort
      }
    }
  }
}
