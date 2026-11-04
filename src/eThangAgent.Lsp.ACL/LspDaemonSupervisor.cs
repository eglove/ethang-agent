using System.Diagnostics;
using System.Runtime.InteropServices;

namespace eThangAgent.Lsp.ACL;

public sealed class LspDaemonSupervisor : IAsyncDisposable
{
  private readonly string _workspaceId;
  private readonly string _workspaceRoot;
  private readonly Func<Process> _startDaemon;
  private Process? _daemon;
  private nint _job;
  private int _restartCount;
  private bool _disposed;

  public LspDaemonSupervisor(string workspaceId, string workspaceRoot, Func<Process> startDaemon)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
    _workspaceId = workspaceId;
    _workspaceRoot = workspaceRoot;
    _startDaemon = startDaemon ?? throw new ArgumentNullException(nameof(startDaemon));
    PipeName = LspPipeNames.ForWorkspaceId(workspaceId);
    State = LspDaemonState.Unavailable;
  }

  public string PipeName { get; }
  public LspDaemonState State { get; private set; }

  public int? DaemonProcessId => _daemon is null || _daemon.HasExited ? null : _daemon.Id;

  public Task EnsureStartedAsync(CancellationToken ct = default)
  {
    ct.ThrowIfCancellationRequested();
    CodeDetectionResult detection = CodeDetector.Detect(_workspaceRoot);
    if (detection == CodeDetectionResult.NotFound)
    {
      State = LspDaemonState.Unavailable;
      return Task.CompletedTask;
    }

    if (_daemon is not null && !_daemon.HasExited)
    {
      State = LspDaemonState.Ready;
      return Task.CompletedTask;
    }

    if (_daemon is not null && _restartCount >= 1)
    {
      State = LspDaemonState.Unavailable;
      return Task.CompletedTask;
    }

    if (_daemon is not null)
    {
      _restartCount++;
    }

    State = LspDaemonState.Starting;
    _daemon = _startDaemon();
    _job = LspJobObject.Create();
    if (_job != nint.Zero && LspJobObject.Configure(_job))
    {
      _ = LspJobObject.Attach(_job, _daemon);
    }
    State = LspDaemonState.Starting;
    return Task.CompletedTask;
  }

  public async Task WaitUntilReadyAsync(CancellationToken ct = default)
  {
    if (State == LspDaemonState.Starting)
    {
      await Task.Delay(10, ct).ConfigureAwait(false);
      State = LspDaemonState.Ready;
    }
    else if (State == LspDaemonState.Ready)
    {
      await Task.CompletedTask.ConfigureAwait(false);
    }
  }

  public void KillForTest()
  {
    if (_daemon is not null && !_daemon.HasExited)
    {
      try { _daemon.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
      _ = _daemon.WaitForExit(5000);
    }
    State = LspDaemonState.Unavailable;
  }

  public async ValueTask DisposeAsync()
  {
    if (_disposed) return;
    _disposed = true;
    if (_daemon is not null && !_daemon.HasExited)
    {
      try { _daemon.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
      _ = _daemon.WaitForExit(5000);
    }
    _daemon?.Dispose();
    _daemon = null;
    if (_job != nint.Zero)
    {
      _ = LspJobObject.Close(_job);
      _job = nint.Zero;
    }
    await ValueTask.CompletedTask.ConfigureAwait(false);
  }
}

internal static class LspJobObject
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

  [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static extern nint CreateJobObjectW(nint lpJobAttributes, string? lpName);

  [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static extern bool SetInformationJobObject(nint hJob, int infoClass, ref ExtendedLimitInfo lpInfo, int cbInfo);

  [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

  [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static extern bool CloseHandle(nint hObject);

  public static nint Create() => CreateJobObjectW(nint.Zero, null);

  public static bool Configure(nint job)
  {
    ExtendedLimitInfo info = default;
    info._basic._limitFlags = KillOnJobClose;
    return SetInformationJobObject(job, ExtendedLimitInfoClass, ref info, Marshal.SizeOf<ExtendedLimitInfo>());
  }

  public static bool Attach(nint job, Process process) => AssignProcessToJobObject(job, process.Handle);

  public static bool Close(nint job) => job != nint.Zero && CloseHandle(job);
}
