using System.Diagnostics;
using System.Text.Json;
using eThangAgent.ToolDomain;
using eThangAgent.Windows.ACL;

namespace eThangAgent.ComputerUse.ACL;

/// <summary>Spawns the broker process: the exe, the pipe name as argv[1], the per-spawn
///     token in ETHANG_COMPUTER_USE_TOKEN. Returns the started process so the supervisor
///     can attach the kill-on-close Job Object. Test seam.</summary>
public delegate Process SpawnHost(string exePath, string pipeName, string token);

/// <summary>One workspace's broker process (task 13): lazy spawn of the Host exe, per-spawn
///     token over ETHANG_COMPUTER_USE_TOKEN, kill-on-close Job Object containment (the broker
///     dies with this process), and ONE lazy restart with short backoff after a healthy
///     connection is lost. A second consecutive crash propagates to the caller. Dispose is a
///     best-effort kill.</summary>
public sealed class BrokerSupervisor(string hostPath, string pipeName, string workspaceId, SpawnHost? spawn = null, NotReadyPolicy? notReady = null, Func<string, string>? hostPathForSpawn = null, bool targetedInput = false) : IAsyncDisposable
{
  private const int ProtocolVersion = 1;
  private const string Platform = "windows";
  private static readonly TimeSpan RestartBackoff = TimeSpan.FromMilliseconds(250);

  public string WorkspaceId { get; } = workspaceId;
  private readonly string _hostPath = hostPath ?? throw new ArgumentNullException(nameof(hostPath));
  private readonly string _pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
  private readonly SpawnHost _spawn = spawn ?? ((exePath, pipe, token) => DefaultSpawn(exePath, pipe, token, targetedInput));
  private readonly NotReadyPolicy? _notReady = notReady;
  private readonly Func<string, string> _hostPathForSpawn = hostPathForSpawn ?? (static path => path);
  private readonly Lock _gate = new();
  private readonly List<(Process Process, nint Job)> _processes = [];
  private NdjsonPipeClient? _client;
  private Task<NdjsonPipeClient>? _connecting;
  private string _token = NewToken();
  private int _crashes;
  private bool _disposed;

  // ---- Integration-test seams (InternalsVisibleTo only): the second-controller test
  // needs the pipe name + token to open a rival client; the broker-kill test needs to kill
  // the spawned process while the supervisor (and its restart budget) survives.
  internal string TestPipeName() => _pipeName;

  internal string TestToken() => _token;

  internal void TestToken(string token) => _token = token;

  // C5 seam contract (fix round 3): the test kill models a REAL crash - it does NOT reset the
  // crash counter (only a SUCCESSFUL round trip resets it, see RoundTripEnvelopeAsync). One kill
  // leaves the budget intact, so the next call respawns lazily and SUCCEEDS; two kills without an
  // intervening success exhaust the budget and the next call surfaces typed HELPER_UNAVAILABLE
  // through the envelope path (never a raw exception).

  /// <summary>Integration-test seam (fix round 4, controller ruling): a rival is a PLAIN
  ///     AUTHENTICATED connection on the EXISTING broker's pipe - authenticate + hello with
  ///     the shared token - NOT a second supervisor and never a second process. The returned
  ///     client speaks the same wire protocol; the pipe's controller lease arbitrates rivalry.</summary>
  public static async Task<NdjsonPipeClient> RivalOn(BrokerSupervisor existing, string? token = null, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(existing);
    return await NdjsonPipeClient
        .ConnectAsync(existing._pipeName, token ?? existing.TestToken(), ProtocolVersion, Platform,
          notReady: existing._notReady, ct: ct)
        .ConfigureAwait(false);
  }

  internal async Task KillBrokerProcessForTests()
  {
    await KillAsync().ConfigureAwait(false);
    _crashes++;
  }

  private static string NewToken() => Guid.NewGuid().ToString("N");

  /// <summary>Default spawn: the Host exe with the pipe name as argv[1], the optional
  ///     <c>--input-targeted</c> flag (TargetedInputDelivery — the no-global-injection
  ///     mode: input travels as window messages to the verified foreground target), and
  ///     the per-spawn token in ETHANG_COMPUTER_USE_TOKEN.
  ///     The child joins a kill-on-close Job Object so an app crash or exit kills the
  ///     broker (no orphaned automation process survives).</summary>
  private static Process DefaultSpawn(string exePath, string pipeName, string token, bool targetedInput)
  {
    if (!File.Exists(exePath))
    {
      throw new BrokerSupervisorException("broker host exe not found: " + exePath);
    }

    // The flag literal mirrors Host's BrokerLaunchOptions.TargetedInputFlag: the Host
    // exe is a separate process the ACL cannot type-reference (the dependency runs
    // Host -> ACL), so the wire strings are duplicated by convention, like the token
    // env var above.
    ProcessStartInfo psi = new(exePath, pipeName + (targetedInput ? " --input-targeted" : string.Empty))
    {
      UseShellExecute = false,
      CreateNoWindow = true,
      EnvironmentVariables =
      {
        ["ETHANG_COMPUTER_USE_TOKEN"] = token,
      },
    };
    return Process.Start(psi) ?? throw new BrokerSupervisorException("broker host failed to start");
  }

  /// <summary>Executes one broker request and returns the RAW reply envelope for callers
  ///     that parse non-receipt results (capture_app, list_*). Same supervision policy as
  ///     the legacy path; transport loss maps to typed failures (C4) so envelope callers
  ///     can never leak an exception into the turn loop.</summary>
  public async Task<BrokerReply> RequestEnvelopeAsync(string method, JsonElement? parameters, CancellationToken ct = default)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    try
    {
      BrokerReply reply = await RoundTripEnvelopeAsync(method, parameters, ct).ConfigureAwait(false);
      return reply;
    }
    catch (BrokerTimeoutException ex)
    {
      throw new BrokerEnvelopeException(ComputerErrorCodes.Timeout, "broker pipe never became ready: " + Inner(ex), ex);
    }
    catch (BrokerSupervisorException ex)
    {
      throw new BrokerEnvelopeException(ComputerErrorCodes.HelperUnavailable, ex.Message, ex);
    }
    catch (Exception ex) when (ex is BrokerConnectionClosedException or BrokerProtocolException or IOException)
    {
      _crashes++;
      if (_crashes > 1)
      {
        throw new BrokerEnvelopeException(ComputerErrorCodes.HelperUnavailable, "broker connection lost twice: " + Inner(ex), ex);
      }

      // ONE lazy restart with short backoff on the NEXT request.
      await KillAsync().ConfigureAwait(false);
      await Task.Delay(RestartBackoff, ct).ConfigureAwait(false);
      _token = NewToken();
      throw new BrokerEnvelopeException(ComputerErrorCodes.HelperUnavailable, "broker connection lost; retry the request (one restart budgeted): " + Inner(ex), ex);
    }
    catch (Exception ex) when (ex is not BrokerEnvelopeException and not OperationCanceledException)
    {
      // Fix round 4 (finding 4): ANY raw failure on the connect path - the committed
      // crash class was JobObject.Attach's InvalidOperationException on an inert
      // Process - is wrapped typed so it can never escape the envelope boundary and
      // never leaves an unexpected exception shape in the turn loop.
      throw new BrokerEnvelopeException(ComputerErrorCodes.HelperUnavailable, "broker connect failed: " + Inner(ex), ex);
    }
  }

  private static string Inner(Exception ex) => ex.InnerException?.Message ?? ex.Message;

  private async Task<BrokerReply> RoundTripEnvelopeAsync(string method, JsonElement? parameters, CancellationToken ct)
  {
    NdjsonPipeClient pipe = await GetClientAsync(ct).ConfigureAwait(false);
    BrokerReply reply = await pipe.RequestAsync(method, parameters, ct).ConfigureAwait(false);
    _crashes = 0;
    return reply;
  }
  private Task<NdjsonPipeClient> GetClientAsync(CancellationToken ct)
  {
    _gate.Enter();
    try
    {
      if (_client is not null)
      {
        return Task.FromResult(_client);
      }

      // Single flight: one spawn+handshake; concurrent first requests await the winner.
      // A faulted connect attempt must NOT stay cached: a failed spawn leaves no client
      // behind, so the cached task would surface the same stale fault forever. Clear it
      // so the next call retries (fix round 4).
      if (_connecting is { IsFaulted: true } or { IsCanceled: true })
      {
        _connecting = null;
      }

      _connecting ??= ConnectAsync(ct);
      return _connecting;
    }
    finally
    {
      _gate.Exit();
    }
  }

  private async Task<NdjsonPipeClient> ConnectAsync(CancellationToken ct)
  {
    Process process = _spawn(_hostPathForSpawn(_hostPath), _pipeName, _token);
    nint job = JobObject.Create();
    if (job != nint.Zero && JobObject.Configure(job))
    {
      _ = JobObject.Attach(job, process);
    }

    _gate.Enter();
    try
    {
      _processes.Add((process, job));
    }
    finally
    {
      _gate.Exit();
    }

    NdjsonPipeClient fresh = await NdjsonPipeClient
        .ConnectAsync(_pipeName, _token, ProtocolVersion, Platform, _notReady, ct).ConfigureAwait(false);
    _gate.Enter();
    try
    {
      _client = fresh;
      _connecting = null;
    }
    finally
    {
      _gate.Exit();
    }

    return fresh;
  }

  private async Task KillAsync()
  {
    NdjsonPipeClient? dead;
    _gate.Enter();
    try
    {
      dead = _client;
      _client = null;
      _connecting = null;
    }
    finally
    {
      _gate.Exit();
    }

    if (dead is not null)
    {
      await dead.DisposeAsync().ConfigureAwait(false);
    }

    foreach ((Process process, nint job) in SnapshotProcesses())
    {
      try
      {
        process.Kill(entireProcessTree: true);
        process.Dispose();
      }
      catch (InvalidOperationException)
      {
        // already exited - best-effort kill per the contract
      }

      if (job != nint.Zero)
      {
        _ = JobObject.Close(job); // closing a kill-on-close job reaps any survivors
      }
    }
  }

  private (Process Process, nint Job)[] SnapshotProcesses()
  {
    _gate.Enter();
    try
    {
      (Process Process, nint Job)[] snapshot = [.. _processes];
      _processes.Clear();
      return snapshot;
    }
    finally
    {
      _gate.Exit();
    }
  }

  /// <inheritdoc />
  public async ValueTask DisposeAsync()
  {
    _disposed = true;
    await KillAsync().ConfigureAwait(false);
  }
}

/// <summary>The broker is gone and the one lazy restart did not recover it: HELPER_UNAVAILABLE territory, carried as a typed error.</summary>
public sealed class BrokerSupervisorException : Exception
{
  public BrokerSupervisorException() : this("broker supervisor failed.") { }
  public BrokerSupervisorException(string message) : base(message) { }
  public BrokerSupervisorException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>A typed transport failure on the envelope path (C4): callers catch this and
///     render ComputerOutcome.Failure from Code+Message - exceptions never escape raw.</summary>

public sealed class BrokerEnvelopeException : Exception
{
  public string Code { get; }

  public BrokerEnvelopeException() : base() => Code = ComputerErrorCodes.HelperUnavailable;

  public BrokerEnvelopeException(string code, string message) : base(message) => Code = code;

  public BrokerEnvelopeException(string code, string message, Exception innerException)
    : base(message, innerException) => Code = code;

  public BrokerEnvelopeException(string message) : base(message) => Code = ComputerErrorCodes.HelperUnavailable;

  public BrokerEnvelopeException(string message, Exception innerException)
    : base(message, innerException) => Code = ComputerErrorCodes.HelperUnavailable;
}

