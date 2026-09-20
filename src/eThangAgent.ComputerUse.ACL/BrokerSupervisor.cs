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
public sealed class BrokerSupervisor(string hostPath, string pipeName, string workspaceId, SpawnHost? spawn = null) : IAsyncDisposable
{
  private const int ProtocolVersion = 1;
  private const string Platform = "windows";
  private static readonly TimeSpan RestartBackoff = TimeSpan.FromMilliseconds(250);


  public string WorkspaceId { get; } = workspaceId;
  private readonly string _hostPath = hostPath ?? throw new ArgumentNullException(nameof(hostPath));
  private readonly string _pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
  private readonly SpawnHost _spawn = spawn ?? DefaultSpawn;
  private readonly Lock _gate = new();
  private readonly List<Process> _processes = [];
  private NdjsonPipeClient? _client;
  private string _token = NewToken();
  private int _crashes;
  private bool _disposed;



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
    Process process = Process.Start(psi) ?? throw new BrokerSupervisorException("broker host failed to start");
    _ = JobObject.Assign(process);
    return process;
  }

  /// <summary>Executes one broker request, spawning or restarting the broker as needed.
  ///     Transport loss maps to HELPER_UNAVAILABLE; a healthy start followed by one crash
  ///     earns exactly one lazy restart before the failure propagates.</summary>
  public async Task<ComputerOutcome> RequestAsync(string method, JsonElement? parameters, CancellationToken ct = default)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    try
    {
      return await RoundTripAsync(method, parameters, ct).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is BrokerConnectionClosedException or BrokerProtocolException or IOException or BrokerSupervisorException)
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

  private async Task<ComputerOutcome> RoundTripAsync(string method, JsonElement? parameters, CancellationToken ct)
  {
    NdjsonPipeClient pipe = await GetClientAsync(ct).ConfigureAwait(false);
    BrokerReply reply = await pipe.RequestAsync(method, parameters, ct).ConfigureAwait(false);
    _crashes = 0;
    return ToOutcome(reply);
  }

  private async Task<NdjsonPipeClient> GetClientAsync(CancellationToken ct)
  {
    _gate.Enter();
    NdjsonPipeClient? existing;
    try
    {
      existing = _client;
      if (existing is not null)
      {
        return existing;
      }
    }
    finally
    {
      _gate.Exit();
    }

    if (existing is not null)
    {
      return existing;
    }

    // Connect outside the lock; the pipe name arbitrates duplicate spawns between racers,
    // and the loser's client is dropped on the next lock pass.
    Process process = _spawn(_hostPath, _pipeName, _token);
    _gate.Enter();
    try
    {
      _processes.Add(process);
    }
    finally
    {
      _gate.Exit();
    }

    NdjsonPipeClient fresh = await NdjsonPipeClient.ConnectAsync(_pipeName, _token, ProtocolVersion, Platform, ct: ct).ConfigureAwait(false);
    _gate.Enter();
    try
    {
      _client ??= fresh;
    }
    finally
    {
      _gate.Exit();
    }

    return fresh;
  }

  private static ComputerOutcome ToOutcome(BrokerReply reply) => reply.Error is { } error
    ? BrokerErrorMapper.Map(error.Code, error.Message)
    : new ComputerOutcome.Receipt(true, "accepted", null, null);

  private async Task KillAsync()
  {
    NdjsonPipeClient? dead;
    _gate.Enter();
    try
    {
      dead = _client;
      _client = null;
    }
    finally
    {
      _gate.Exit();
    }

    if (dead is not null)
    {
      await dead.DisposeAsync().ConfigureAwait(false);
    }

    foreach (Process process in SnapshotProcesses())
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
    }
  }

  private Process[] SnapshotProcesses()
  {
    _gate.Enter();
    try
    {
      Process[] snapshot = [.. _processes];
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
    private static extern nint CreateJobObjectW(nint lpJobAttributes, string lpName);
#pragma warning restore SYSLIB1054

#pragma warning disable SYSLIB1054 // Named decision: LibraryImport needs unsafe blocks; DllImport with blittable layouts marshals identically here.
    [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool SetInformationJobObject(nint hJob, int infoClass, ref ExtendedLimitInfo lpInfo, int cbInfo);
#pragma warning restore SYSLIB1054

#pragma warning disable SYSLIB1054 // Named decision: LibraryImport needs unsafe blocks; DllImport with blittable layouts marshals identically here.
    [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);
#pragma warning restore SYSLIB1054

    /// <summary>Creates the kill-on-close job and assigns the process. True on success.
    ///     </summary>
    public static bool Assign(Process process)
    {
      nint job = CreateJobObjectW(nint.Zero, "ethang-computer-use-broker");
      if (job == nint.Zero)
      {
        return false;
      }

      ExtendedLimitInfo info = default;
      info._basic._limitFlags = KillOnJobClose;
      return SetInformationJobObject(job, ExtendedLimitInfoClass, ref info, Marshal.SizeOf<ExtendedLimitInfo>())
        && AssignProcessToJobObject(job, process.Handle);
    }
  }
}

/// <summary>The broker is gone and the one lazy restart did not recover it: HELPER_UNAVAILABLE territory, carried as a typed error.</summary>
public sealed class BrokerSupervisorException : Exception
{
  public BrokerSupervisorException() : this("broker supervisor failed.") { }
  public BrokerSupervisorException(string message) : base(message) { }
  public BrokerSupervisorException(string message, Exception innerException) : base(message, innerException) { }
}
