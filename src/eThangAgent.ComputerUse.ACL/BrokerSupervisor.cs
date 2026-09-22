using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using eThangAgent.ToolDomain;

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
public sealed class BrokerSupervisor(string hostPath, string pipeName, string workspaceId, SpawnHost? spawn = null, NotReadyPolicy? notReady = null) : IAsyncDisposable
{
  private const int ProtocolVersion = 1;
  private const string Platform = "windows";
  private static readonly TimeSpan RestartBackoff = TimeSpan.FromMilliseconds(250);


  public string WorkspaceId { get; } = workspaceId;
  private readonly string _hostPath = hostPath ?? throw new ArgumentNullException(nameof(hostPath));
  private readonly string _pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
  private readonly SpawnHost _spawn = spawn ?? DefaultSpawn;
  private readonly NotReadyPolicy? _notReady = notReady;
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

  internal async Task KillBrokerProcessForTests()
  {
    await KillAsync().ConfigureAwait(false);
    _crashes = 0; // the restart budget is judged by the supervisor itself, not the test kill
  }

  private static string NewToken() => Guid.NewGuid().ToString("N");

  /// <summary>Default spawn: the Host exe with the pipe name as argv[1] and the per-spawn
  ///     token in ETHANG_COMPUTER_USE_TOKEN. The child joins a kill-on-close Job Object so an
  ///     app crash or exit kills the broker (no orphaned automation process survives).</summary>
  private static Process DefaultSpawn(string exePath, string pipeName, string token)
  {
    if (!File.Exists(exePath))
    {
      throw new BrokerSupervisorException("broker host exe not found: " + exePath);
    }

    ProcessStartInfo psi = new(exePath, pipeName)
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

  /// <summary>Executes one broker request, spawning or restarting the broker as needed.
  ///     Transport loss maps to HELPER_UNAVAILABLE; a healthy start followed by one crash
  ///     earns exactly one lazy restart before the failure propagates.</summary>
  /// <summary>Executes one broker request and returns the RAW reply envelope for
  ///     callers that parse non-receipt results (capture_app, list_*). The same
  ///     supervision policy applies: lazy spawn, transport-loss mapping, one lazy
  ///     restart. Errors travel IN the envelope (BrokerReply.Error), transport loss
  ///     as the thrown typed exceptions RequestAsync also throws.</summary>
  public async Task<BrokerReply> RequestEnvelopeAsync(string method, JsonElement? parameters, CancellationToken ct = default)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    try
    {
      BrokerReply reply = await RoundTripEnvelopeAsync(method, parameters, ct).ConfigureAwait(false);
      return reply;
    }
    catch (Exception ex) when (ex is BrokerSupervisorException or BrokerTimeoutException)
    {
      throw;
    }
    catch (Exception ex) when (ex is BrokerConnectionClosedException or BrokerProtocolException or IOException)
    {
      _crashes++;
      if (_crashes > 1)
      {
        throw new BrokerSupervisorException("broker connection lost twice: " + Inner(ex), ex);
      }

      // ONE lazy restart with short backoff on the NEXT request.
      await KillAsync().ConfigureAwait(false);
      await Task.Delay(RestartBackoff, ct).ConfigureAwait(false);
      _token = NewToken();
      throw new BrokerSupervisorException("broker connection lost; retry the request (one restart budgeted): " + Inner(ex), ex);
    }
  }
  public async Task<ComputerOutcome> RequestAsync(string method, JsonElement? parameters, CancellationToken ct = default)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    try
    {
      return await RoundTripAsync(method, parameters, ct).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is BrokerSupervisorException or BrokerTimeoutException)
    {
      // Configuration or readiness problems: nothing was sent, no restart budget burned.
      return ex is BrokerTimeoutException
        ? new ComputerOutcome.Failure(ComputerErrorCodes.Timeout, "broker pipe never became ready: " + Inner(ex))
        : new ComputerOutcome.Failure(ComputerErrorCodes.HelperUnavailable, Inner(ex));
    }
    catch (Exception ex) when (ex is BrokerConnectionClosedException or BrokerProtocolException or IOException)
    {
      _crashes++;
      if (_crashes > 1)
      {
        return new ComputerOutcome.Failure(ComputerErrorCodes.HelperUnavailable, "broker connection lost twice: " + Inner(ex));
      }

      // ONE lazy restart with short backoff on the NEXT request.
      await KillAsync().ConfigureAwait(false);
      await Task.Delay(RestartBackoff, ct).ConfigureAwait(false);
      _token = NewToken();
      return new ComputerOutcome.Failure(ComputerErrorCodes.HelperUnavailable, "broker connection lost; retry the request (one restart budgeted): " + Inner(ex));
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
  private async Task<ComputerOutcome> RoundTripAsync(string method, JsonElement? parameters, CancellationToken ct)
  {
    NdjsonPipeClient pipe = await GetClientAsync(ct).ConfigureAwait(false);
    BrokerReply reply = await pipe.RequestAsync(method, parameters, ct).ConfigureAwait(false);
    _crashes = 0;
    return ToOutcome(reply);
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
    Process process = _spawn(_hostPath, _pipeName, _token);
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

  private static ComputerOutcome ToOutcome(BrokerReply reply)
  {
    if (reply.Error is { } error)
    {
      return BrokerErrorMapper.Map(error.Code, error.Message);
    }

    // Echo the wire receipt honestly: action_sent=false must survive to the surface.
    return BrokerActionReceipt.From(reply) is { } receipt
      ? new ComputerOutcome.Receipt(receipt.ActionSent, receipt.DispatchStatus, receipt.EffectEvidence, null)
      : new ComputerOutcome.Failure(ComputerErrorCodes.Internal, "broker returned an unrecognized result shape.");
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

  /// <summary>Windows Job Object containment (no unsafe): the broker is assigned to a
  ///     kill-on-close job, so app crash or exit closes the job handle and the kernel kills
  ///     the broker. Best-effort: assignment failure degrades to explicit-kill disposal.</summary>
  private static partial class JobObject
  {
    private const int ExtendedLimitInfoClass = 9;
    private const uint KillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInfo
    {
      public long _perProcessUserTimeLimit;
      public long _perJobUserTimeLimit;
      public uint _limitFlags;
      public nuint _minimumWorkingSetSize;
      public nuint _maximumWorkingSetSize;
      public uint _activeProcessLimit;
      public nuint _affinity;
      public uint _priorityClass;
      public uint _schedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
      public ulong _readOperationCount;
      public ulong _writeOperationCount;
      public ulong _otherOperationCount;
      public ulong _readTransferCount;
      public ulong _writeTransferCount;
      public ulong _otherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInfo
    {
      public BasicLimitInfo _basic;
      public IoCounters _ioInfo;
      public nuint _processMemoryLimit;
      public nuint _jobMemoryLimit;
      public nuint _peakProcessMemoryUsed;
      public nuint _peakJobMemoryUsed;
    }

#pragma warning disable SYSLIB1054 // Named decision: LibraryImport needs unsafe blocks; DllImport with blittable layouts marshals identically here.
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint CreateJobObjectW(nint lpJobAttributes, string? lpName);
#pragma warning restore SYSLIB1054

#pragma warning disable SYSLIB1054 // Named decision: LibraryImport needs unsafe blocks; DllImport with blittable layouts marshals identically here.
    [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool SetInformationJobObject(nint hJob, int infoClass, ref ExtendedLimitInfo lpInfo, int cbInfo);
#pragma warning restore SYSLIB1054

#pragma warning disable SYSLIB1054 // Named decision: LibraryImport needs unsafe blocks; DllImport with blittable layouts marshals identically here.
    [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);
#pragma warning restore SYSLIB1054
    /// <summary>Creates an UNNAMED kill-on-close job (controller ruling): per-spawn
    ///     containment, no cross-instance crosstalk.</summary>
    public static nint Create() => CreateJobObjectW(nint.Zero, null);

    public static bool Configure(nint job)
    {
      ExtendedLimitInfo info = default;
      info._basic._limitFlags = KillOnJobClose;
      return SetInformationJobObject(job, ExtendedLimitInfoClass, ref info, Marshal.SizeOf<ExtendedLimitInfo>());
    }

    public static bool Attach(nint job, Process process) => AssignProcessToJobObject(job, process.Handle);

#pragma warning disable SYSLIB1054 // Named decision: same rationale as above.
    [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CloseHandle(nint hObject);
#pragma warning restore SYSLIB1054

    /// <summary>Closes the job handle; a kill-on-close job reaps any surviving children.</summary>
#pragma warning disable S4200 // Named decision: the guard makes this wrapper meaningful (zero handle never passed to native).
    public static bool Close(nint job) => job != nint.Zero && CloseHandle(job);
#pragma warning restore S4200

  }
}

/// <summary>The broker is gone and the one lazy restart did not recover it: HELPER_UNAVAILABLE territory, carried as a typed error.</summary>
public sealed class BrokerSupervisorException : Exception
{
  public BrokerSupervisorException() : this("broker supervisor failed.") { }
  public BrokerSupervisorException(string message) : base(message) { }
  public BrokerSupervisorException(string message, Exception innerException) : base(message, innerException) { }
}
