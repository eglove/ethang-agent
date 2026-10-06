using System.Diagnostics;
using System.Text.Json;
using eThangAgent.ToolDomain.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace eThangAgent.Mcp.ACL;

/// <summary>The SDK-backed connect seam (issues #104/#105): builds the transport for
///     one configured server and creates the client session over it. stdio servers
///     spawn CONTAINED (issue #105): the harness starts the process itself - the SDK's
///     StdioClientTransport offers no process-started hook, so a kill-on-close Job
///     Object could never attach to its spawn - with the environment allowlisted (B1:
///     SDK defaults plus the server's configured entries, never the parent process
///     env), the job attached, and stderr pumped into a bounded tail (B3); the
///     process's streams ride the SDK's StreamClientTransport for the protocol work.
///     HTTP servers carry static headers; OAuth is a later increment (#110). Dispose
///     reaps every session this factory created (the workspace pool's lifetime).</summary>
public sealed class SdkMcpClientSessionFactory : IMcpClientSessionPool, IAsyncDisposable, IDisposable
{
  private readonly Lock _gate = new();
  private readonly List<ContainedStdioSession> _stdioSessions = [];

  /// <summary>Test seam: observes every stdio server Process this factory spawned
  ///     (the reaping tests hold the object, whose HasExited/WaitForExitAsync stay
  ///     valid after the process dies). Null in production.</summary>
  internal Action<Process>? TestServerProcessSpawned { get; set; }

  /// <summary>Builds the stdio transport options for one server config: command,
  ///     args, name, and the environment allowlist (defaults + configured, never the
  ///     parent process env). Internal for tests; the shape is the ACL's contract,
  ///     and the guard's spawn consumes it verbatim.</summary>
  internal static StdioClientTransportOptions BuildStdioOptions(McpServerConfig server)
  {
    ArgumentNullException.ThrowIfNull(server);
    StdioClientTransportOptions options = new()
    {
      Command = server.CommandOrUrl,
      Name = server.Name,
      InheritEnvironmentVariables = false,
    };

    if (server.ArgsJson.Length > 2)
    {
      using JsonDocument args = JsonDocument.Parse(server.ArgsJson);
      List<string> parsed = [];
      foreach (JsonElement item in args.RootElement.EnumerateArray())
      {
        parsed.Add(item.ValueKind == JsonValueKind.String ? item.GetString()! : item.GetRawText());
      }

      options.Arguments = parsed;
    }

    Dictionary<string, string?> env = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
    if (server.EnvJson.Length > 2)
    {
      using JsonDocument configured = JsonDocument.Parse(server.EnvJson);
      foreach (JsonProperty property in configured.RootElement.EnumerateObject())
      {
        env[property.Name] = property.Value.ValueKind == JsonValueKind.String
            ? property.Value.GetString()
            : property.Value.GetRawText();
      }
    }

    options.EnvironmentVariables = env;
    return options;
  }

  /// <summary>Builds the Streamable-HTTP transport options for one server config:
  ///     the endpoint plus the static headers from the row. OAuth is a later
  ///     increment (issue #110); ClientOAuthOptions stays unset.</summary>
  internal static HttpClientTransportOptions BuildHttpOptions(McpServerConfig server)
  {
    ArgumentNullException.ThrowIfNull(server);
    HttpClientTransportOptions options = new()
    {
      Endpoint = new Uri(server.CommandOrUrl),
      Name = server.Name,
      TransportMode = HttpTransportMode.StreamableHttp,
    };

    if (server.HeadersJson.Length > 2)
    {
      using JsonDocument headers = JsonDocument.Parse(server.HeadersJson);
      Dictionary<string, string> parsed = [];
      foreach (JsonProperty property in headers.RootElement.EnumerateObject())
      {
        parsed[property.Name] = property.Value.ValueKind == JsonValueKind.String
            ? property.Value.GetString() ?? string.Empty
            : property.Value.GetRawText();
      }

      options.AdditionalHeaders = parsed;
    }

    return options;
  }

  /// <summary>Creates the transport for one server config: stdio spawn or Streamable
  ///     HTTP. Internal for tests.</summary>
  internal static IClientTransport CreateTransport(McpServerConfig server) =>
      server.Transport == McpTransport.Stdio
          ? new StdioClientTransport(BuildStdioOptions(server))
          : new HttpClientTransport(BuildHttpOptions(server));

  /// <inheritdoc />
  public Task<McpConnectResult> ConnectAsync(McpServerConfig server, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(server);
    return server.Transport == McpTransport.Stdio
        ? ConnectStdioContainedAsync(BuildStdioOptions(server), ct)
        : ConnectOverTransportAsync(() => new HttpClientTransport(BuildHttpOptions(server)), ct);
  }

  /// <summary>Connects over a transport the caller builds (the in-memory test hook:
  ///     tests swap the transport for a StreamClientTransport over duplex streams
  ///     without any process spawn). The HTTP production path rides it too.</summary>
  internal static async Task<McpConnectResult> ConnectOverTransportAsync(
      Func<IClientTransport> transportFactory, CancellationToken ct)
  {
#pragma warning disable CA2000 // named decision: session ownership transfers to the pool
    try
    {
      IClientTransport transport = transportFactory();
      McpClient client = await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
      SdkMcpClientSession session = new(client);
      return new McpConnectResult.Success(session);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
      throw;
    }
#pragma warning disable CA1031 // named decision: every transport failure (refused HTTP, protocol error) is a connect failure value, never an exception escaping to the pool
    catch (Exception ex)
    {
      return new McpConnectResult.Failure("McpConnectFailed", ex.Message);
    }
#pragma warning restore CA1031
#pragma warning restore CA2000
  }

  /// <summary>Connects a stdio server CONTAINEDLY (issue #105): spawns the process
  ///     with the allowlisted environment, attaches the kill-on-close job, pumps
  ///     stderr, and runs the SDK protocol over the process streams. A handshake
  ///     failure reaps the spawned process before the failure value returns.</summary>
  internal async Task<McpConnectResult> ConnectStdioContainedAsync(
      StdioClientTransportOptions options, CancellationToken ct)
  {
    Process? process = null;
    nint job = nint.Zero;
    StderrTail stderr = new();
    try
    {
      process = SpawnServer(options, stderr);
      TestServerProcessSpawned?.Invoke(process);
      job = JobObject.Create();
      if (job != nint.Zero && JobObject.Configure(job))
      {
        _ = JobObject.Attach(job, process);
      }
      else
      {
        // Named decision: job failure degrades to explicit-kill disposal (the
        // broker's precedent) - containment weakens, the session still works.
        job = nint.Zero;
      }

      // StreamClientTransport(serverInput, serverOutput): the client writes requests
      // to the server's stdin and reads responses from the server's stdout.
      StreamClientTransport transport = new(process.StandardInput.BaseStream, process.StandardOutput.BaseStream);
      McpClient client = await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
      ContainedStdioSession session = new(client, process, job, stderr);
      lock (_gate)
      {
        _stdioSessions.Add(session);
      }

      return new McpConnectResult.Success(session);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
      Reap(process, job);
      throw;
    }
#pragma warning disable CA1031 // named decision: every containment-path failure (spawn refusal, handshake fault, protocol error) is a connect failure value, never an exception escaping to the pool
    catch (Exception ex)
    {
      Reap(process, job);
      return new McpConnectResult.Failure("McpConnectFailed", ex.Message);
    }
#pragma warning restore CA1031
  }

  /// <summary>Reaps a failed containment attempt: kills the spawned process tree and
  ///     closes the job (a kill-on-close job reaps any survivor).</summary>
  private static void Reap(Process? process, nint job)
  {
    if (process is not null)
    {
      try
      {
        if (!process.HasExited)
        {
          process.Kill(entireProcessTree: true);
        }
      }
#pragma warning disable CA1031 // named decision: a reaped process cannot be killed twice; reaping is best-effort by contract
      catch (Exception)
      {
        // already exited or already reaped
      }
#pragma warning restore CA1031

      process.Dispose();
    }

    _ = JobObject.Close(job);
  }

  /// <summary>Spawns the server process: the command plus its arguments, the
  ///     allowlisted environment (defaults + configured - never the parent's), stderr
  ///     pumped into the tail, stdout/stdin redirected for the protocol streams.</summary>
  private static Process SpawnServer(StdioClientTransportOptions options, StderrTail stderr)
  {
    ProcessStartInfo psi = new()
    {
      FileName = options.Command,
      UseShellExecute = false,
      CreateNoWindow = true,
      RedirectStandardInput = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
    };
    if (options.Arguments is { Count: > 0 })
    {
      foreach (string argument in options.Arguments)
      {
        psi.ArgumentList.Add(argument);
      }
    }

    // B1: ProcessStartInfo.Environment is SEEDED from the parent's environment -
    // populating the allowlist without clearing would leak every parent variable
    // (the seeded decoy proved it). Clear first, then the allowlist is the whole env.
    psi.Environment.Clear();
    if (options.EnvironmentVariables is not null)
    {
      foreach (KeyValuePair<string, string?> entry in options.EnvironmentVariables)
      {
        psi.Environment[entry.Key] = entry.Value;
      }
    }

    Process process = Process.Start(psi)
        ?? throw new InvalidOperationException("the MCP server process failed to start.");
    process.ErrorDataReceived += (_, args) =>
    {
      if (args.Data is not null)
      {
        stderr.Append(args.Data);
      }
    };
    process.BeginErrorReadLine();
    return process;
  }

  /// <inheritdoc />
  public async ValueTask DisposeAsync()
  {
    ContainedStdioSession[] sessions;
    lock (_gate)
    {
      sessions = [.. _stdioSessions];
      _stdioSessions.Clear();
    }

    foreach (ContainedStdioSession session in sessions)
    {
      await session.DisposeAsync().ConfigureAwait(false);
    }
  }

  /// <summary>Sync dispose for the DI container's shutdown path: reaps every
  ///     contained server (the containment-critical part - kill + job close - is
  ///     synchronous by nature) and disposes the SDK clients. Named decision: the
  ///     client dispose awaits on the threadpool; its continuations run without a
  ///     sync context (ConfigureAwait(false) throughout the SDK), so this cannot
  ///     deadlock a UI-thread container close.</summary>
  public void Dispose()
  {
    ContainedStdioSession[] sessions;
    lock (_gate)
    {
      sessions = [.. _stdioSessions];
      _stdioSessions.Clear();
    }

    foreach (ContainedStdioSession session in sessions)
    {
      // Named decision: the threadpool hop keeps a UI-thread container close from
      // deadlocking; the SDK's dispose continuations carry no sync context.
      Task dispose = Task.Run(() => session.DisposeAsync().AsTask());
      dispose.GetAwaiter().GetResult();
    }
  }
}
