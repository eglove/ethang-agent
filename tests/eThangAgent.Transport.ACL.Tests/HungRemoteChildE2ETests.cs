using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>W1.1: the hung-remote-child E2E through the REAL host process. The host is
///     launched with a settings JSON whose SubAgent:Watchdog section (W1.2) sets a
///     small idle threshold, and whose OpenRouter endpoint points at a mock provider
///     that ACCEPTS the child's first request and then HANGS (requests accepted,
///     never answered). The watchdog runs HOST-side; every audit row in the shared
///     database is therefore authored by the host process (the test process only
///     READS). Assertions per the spec: HungDetected then RetrySpawned audit rows for
///     the child id; the child settles Failed(Hung) after the wrap-up retry also
///     breaches; the settle envelope reaches the attached app runtime. Operational
///     discipline: the host is spawned DETACHED (output drained on background tasks
///     into a bounded queue), every await deadline-bounded (deadlock vigilance).</summary>
public class HungRemoteChildE2ETests
{
  private static string FindHostExe()
  {
    DirectoryInfo? dir = new(typeof(HungRemoteChildE2ETests).Assembly.Location);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "eThangAgent.slnx")))
    {
      dir = dir.Parent;
    }

    string repo = dir!.FullName;
    return Path.Combine(repo, "src", "eThangAgent.ChildHost", "bin", "Debug", "net10.0", "eThangAgent.ChildHost.exe");
  }

  /// <summary>A one-request provider mock: the FIRST chat request is answered with a
  ///     completion (the child's first turn completes); every later chat request is
  ///     accepted and NEVER answered — the hang. The models endpoint must answer or
  ///     the host's spawn fails on an unknown context window before any call.</summary>
  private sealed class HangAfterFirstRequestServer : IDisposable
  {
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    public Uri BaseUrl { get; private set; } = null!;
    private int _answeredRequests;

    public void Start()
    {
      int port = GetFreePort();
      BaseUrl = new Uri($"http://127.0.0.1:{port}/");
      _listener.Prefixes.Add(BaseUrl.AbsoluteUri);
      _listener.Start();
      _ = Task.Run(LoopAsync, _cts.Token);
    }

    // Named decision (CA1031): the accept loop is the mock's liveness boundary — any
    // single accept fault (listener race at shutdown) must not end the mock server.
#pragma warning disable CA1031 // Do not catch general exception types
    private async Task LoopAsync()
    {
      while (!_cts.IsCancellationRequested)
      {
        HttpListenerContext ctx;
        try
        {
          ctx = await _listener.GetContextAsync().ConfigureAwait(false);
        }
        catch
        {
          break;
        }

        try
        {
          await HandleAsync(ctx).ConfigureAwait(false);
        }
        catch
        {
          // Named decision (CA1031): the mock is a test rig — one failed exchange never
          // takes down the listener; the next request still gets its hang or its answer.
        }
      }
#pragma warning restore CA1031
    }
    private async Task HandleAsync(HttpListenerContext ctx)
    {
      if (ctx.Request.Url!.AbsolutePath == "/api/v1/models")
      {
        string catalog = /*lang=json,strict*/ "{ \"data\": [ { \"id\": \"m/sub\", \"pricing\": { \"prompt\": \"0.000001\", \"completion\": \"0.000002\" }, \"context_length\": 32768, \"top_provider\": { \"max_completion_tokens\": 8192 }, \"architecture\": { \"modality\": \"text->text\" } } ] }";
        await WriteAsync(ctx, 200, "application/json", catalog).ConfigureAwait(false);
        return;
      }

      if (ctx.Request.Url.AbsolutePath == "/api/v1/chat/completions")
      {
        using StreamReader reader = new(ctx.Request.InputStream);
        _ = await reader.ReadToEndAsync(_cts.Token).ConfigureAwait(false);
        int answered = Interlocked.Increment(ref _answeredRequests);
        if (answered == 1)
        {
          string body = /*lang=json,strict*/ "{ \"choices\": [ { \"message\": { \"content\": null, \"tool_calls\": [ { \"id\": \"call_1\", \"type\": \"function\", \"function\": { \"name\": \"web_fetch\", \"arguments\": \"{\\\"url\\\":\\\"http://example.test/\\\"}\" } } ] } } ] }";
          await WriteAsync(ctx, 200, "application/json", body).ConfigureAwait(false);
          return;
        }

        // THE HANG: the request is accepted but never answered — the child's provider
        // call blocks and no progress event ever fires again.
        _ = Task.Run(async () =>
        {
          try
          {
            await Task.Delay(Timeout.InfiniteTimeSpan, _cts.Token).ConfigureAwait(false);
          }
          catch (OperationCanceledException)
          {
            // Rig shutdown: the abandoned requester goes away with the listener.
          }
        }, _cts.Token);
        return;
      }

      ctx.Response.StatusCode = 404;
      ctx.Response.Close();
    }

    private static async Task WriteAsync(HttpListenerContext ctx, int status, string contentType, string body)
    {
      byte[] bytes = Encoding.UTF8.GetBytes(body);
      ctx.Response.StatusCode = status;
      ctx.Response.ContentType = contentType;
      ctx.Response.ContentLength64 = bytes.Length;
      await ctx.Response.OutputStream.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
      ctx.Response.Close();
    }

    private static int GetFreePort()
    {
      using TcpListener listener = new(IPAddress.Loopback, 0);
      listener.Start();
      return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public void Dispose()
    {
      _cts.Cancel();
      _cts.Dispose();
      _listener.Stop();
      _listener.Close();
    }
  }

  /// <summary>DETACHED host launch: stdout/stderr drained on background tasks into a
  ///     bounded queue (an undrained or attached rig wedges the in-process runner).</summary>
  private static async Task<(Process Host, ConcurrentQueue<string> Log)> LaunchHostDetachedAsync(
      string hostExe, string pipeName, string settingsPath, string databasePath)
  {
    ProcessStartInfo psi = new(hostExe);
    psi.ArgumentList.Add(pipeName);
    psi.ArgumentList.Add(settingsPath);
    psi.ArgumentList.Add(databasePath);
    psi.RedirectStandardOutput = true;
    psi.RedirectStandardError = true;
    psi.UseShellExecute = false;
    psi.CreateNoWindow = true;
    Process host = Process.Start(psi) ?? throw new InvalidOperationException("child host failed to start.");
    ConcurrentQueue<string> log = new();
    _ = Task.Run(async () =>
    {
      while (await host.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(true) is { } line)
      {
        log.Enqueue(line);
      }
    }, CancellationToken.None);
    _ = Task.Run(async () =>
    {
      while (await host.StandardError.ReadLineAsync(CancellationToken.None).ConfigureAwait(true) is { } line)
      {
        log.Enqueue("ERR: " + line);
      }
    }, CancellationToken.None);

    // Bounded startup handshake: the host prints host-starting once its pipe exists.
    for (int i = 0; i < 100 && !host.HasExited; i++)
    {
      if (log.Any(l => l.StartsWith("host-starting", StringComparison.Ordinal)))
      {
        break;
      }

      await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    Assert.False(host.HasExited, "host exited during startup: " + string.Join(" | ", log));
    return (host, log);
  }

  private static string SettingsJson(string mockBaseUrl)
      => "{ \"OpenRouter\": { \"ApiKey\": \"sk-test\", \"BaseUrl\": \"" + mockBaseUrl + "\" },"
         + " \"Zai\": { \"ApiKey\": null, \"BaseUrl\": \"http://zai.test\" },"
         + " \"SubAgents\": { \"MaxConcurrentAgents\": 2 },"
         + " \"Watchdog\": { \"TickInterval\": \"00:00:01\", \"IdleThreshold\": \"00:00:02\", \"SettleWait\": \"00:00:05\" } }";

  [Fact]
  public async Task HungRemoteChild_IsInterruptedAndRetriedByTheHost_ThenFailsHung()
  {
    string hostExe = FindHostExe();
    Assert.True(File.Exists(hostExe), "child host exe not built");

    using HangAfterFirstRequestServer mock = new();
    mock.Start();

    string scratch = Path.Combine(Path.GetTempPath(), "e2e-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(scratch);
    string databasePath = Path.Combine(scratch, "db.sqlite");
    await File.WriteAllTextAsync(Path.Combine(scratch, "settings.json"), SettingsJson(mock.BaseUrl.ToString()),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    string pipeName = "ethang-hung-" + Guid.NewGuid().ToString("N");
    (Process host, ConcurrentQueue<string> hostLog) = await LaunchHostDetachedAsync(
        hostExe, pipeName, Path.Combine(scratch, "settings.json"), databasePath).ConfigureAwait(true);
    _ = hostLog; // drained continuously; kept for failure diagnosis
    try
    {
      // The APP side: a real RemoteAgentRuntime over the wire (the ReAttach pattern).
      NamedPipeChildTransport appTransport = await NamedPipeChildTransport.ConnectToHostAsync(pipeName,
          TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
      RemoteAgentRuntime runtime = new(appTransport);
      using CancellationTokenSource loop = new();
      _ = runtime.RunReceiveLoopAsync(loop.Token);

      // Persist the child record the host will run, then start it over the wire.
      SqliteAgentStore bootstrap = new(new AppDatabase(databasePath));
      AgentId child = new(Guid.NewGuid());
      _ = await bootstrap.SaveAsync(AgentRecord.Spawned(child, null, 1, "m/sub", "hung-e2e",
          "you will hang", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken).ConfigureAwait(true);
      Result<AgentRecord> loadedChild = await bootstrap.GetAsync(child, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(loadedChild.IsSuccess, loadedChild.Error?.Message);
      Result<AgentId> start = await runtime.Start(loadedChild.Value, TestContext.Current.CancellationToken)
          .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(start.IsSuccess, start.Error?.Message);
      // The run's first provider call is answered; the second HANGS. The host watchdog
      // must act: idle past the 2-second threshold -> interrupt -> wrap-up retry, which
      // hangs again -> terminal Failed(Hung). The WhenSettledAsync await resolves only
      // when the app's pump receives the FINAL settle envelope (never a poll).
      Result<AgentRunOutcome> outcome = await runtime.WhenSettledAsync(child, TestContext.Current.CancellationToken)
          .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(outcome.IsSuccess, "the final settle envelope never reached the app runtime");
      Assert.Equal(AgentStatus.Failed, outcome.Value.Status);
      Assert.Equal(AgentFailureReason.ProviderError, outcome.Value.Reason); // the cancelled retry attempt's classification


      // Audit rows were written by the HOST process: the test process never writes
      // watchdog_events to this database, so any row present is host-authored by
      // containment. Expect: HungDetected (breach 1), RetrySpawned, HungDetected
      // (breach 2), TerminalReport.
      SqliteWatchdogEventStore audit = new(new AppDatabase(databasePath));
      List<WatchdogEvent> rows = [];
      for (int i = 0; i < 100; i++)
      {
        Result<IReadOnlyList<WatchdogEvent>> recent = await audit.ListRecentAsync(100, TestContext.Current.CancellationToken).ConfigureAwait(true);
        if (recent.IsSuccess)
        {
          rows = [.. recent.Value];
          if (rows.Count(e => e.AgentId == child && e.Kind is WatchdogEventKind.HungDetected) >= 2
              && rows.Any(e => e.AgentId == child && e.Kind is WatchdogEventKind.RetrySpawned)
              && rows.Any(e => e.AgentId == child && e.Kind is WatchdogEventKind.TerminalReport))
          {
            break;
          }
        }

        await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken).ConfigureAwait(true);
      }

      // HungDetected fires once per idle EPISODE (breach1, breach2) plus once more on the
      // terminal path itself (it re-detects before enacting) — hence >= 2, not exactly 2.
      Assert.True(rows.Count(e => e.AgentId == child && e.Kind is WatchdogEventKind.HungDetected) >= 2, "both breaches must be audited as HungDetected");
      Assert.Contains(rows, e => e.AgentId == child && e.Kind is WatchdogEventKind.RetrySpawned); // the wrap-up retry
      Assert.Contains(rows, e => e.AgentId == child && e.Kind is WatchdogEventKind.TerminalReport); // marked Failed(Hung)

      // The record itself carries the terminal outcome the host wrote.
      SqliteAgentStore store = new(new AppDatabase(databasePath));
      Result<AgentRecord> record = await store.GetAsync(child, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(record.IsSuccess);
      Assert.Equal(AgentStatus.Failed, record.Value.Status);
      Assert.Equal(AgentFailureReason.Hung, record.Value.FailureReason);
    }
    finally
    {
      if (!host.HasExited)
      {
        host.Kill(entireProcessTree: true);
      }

      host.Dispose();
    }
  }
}
