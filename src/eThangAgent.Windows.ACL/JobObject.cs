using System.Diagnostics;
using System.Runtime.InteropServices;

namespace eThangAgent.Windows.ACL;

/// <summary>Windows Job Object containment for the processes the app spawns: a process
///     assigned to a kill-on-close job dies when the job handle closes, which is what the
///     kernel does to every handle a process holds when that process exits - cleanly or by
///     crashing. That is the only guarantee that survives the app having no chance to run
///     cleanup code.
///     <para>Best-effort by contract: a job that cannot be created or configured degrades to
///     the caller's explicit-kill disposal, which still covers a clean exit. Only the crash
///     path depends on the kernel.</para>
///     <para>Every job is UNNAMED and created per spawn, so containment is exact: closing one
///     job reaps its own processes and no others.</para></summary>
public static class JobObject
{
  private const int ExtendedLimitInfoClass = 9;
  private const uint KillOnJobClose = 0x2000;
  private const uint LimitProcessMemory = 0x00000100;

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
  private static extern bool IsProcessInJob(nint processHandle, nint jobHandle, out int result);

  [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static extern bool CloseHandle(nint hObject);

  /// <summary>Creates an unnamed job with no limits configured yet.</summary>
  public static nint Create() => CreateJobObjectW(nint.Zero, null);

  /// <summary>Configures kill-on-close: the kernel reaps the job's processes when the
  ///     handle closes.</summary>
  public static bool Configure(nint job)
  {
    ExtendedLimitInfo info = default;
    info._basic._limitFlags = KillOnJobClose;
    return SetInformationJobObject(job, ExtendedLimitInfoClass, ref info, Marshal.SizeOf<ExtendedLimitInfo>());
  }

  /// <summary>Configures kill-on-close plus a per-process memory ceiling, so a child that
  ///     outgrows its budget is killed by the kernel rather than by whoever is watching it.
  ///     A refused limit degrades to guard-only enforcement.</summary>
  public static bool ConfigureWithMemoryLimit(nint job, long memoryLimitBytes)
  {
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memoryLimitBytes);
    ExtendedLimitInfo info = default;
    info._basic._limitFlags = KillOnJobClose | LimitProcessMemory;
    info._processMemoryLimit = (nuint)memoryLimitBytes;
    return SetInformationJobObject(job, ExtendedLimitInfoClass, ref info, Marshal.SizeOf<ExtendedLimitInfo>());
  }

  /// <summary>Assigns a started process to the job. Throws <see cref="InvalidOperationException"/>
  ///     when the process handle is unusable, like every other member of
  ///     <see cref="Process"/>.</summary>
  public static bool Attach(nint job, Process process)
  {
    ArgumentNullException.ThrowIfNull(process);
    return AssignProcessToJobObject(job, process.Handle);
  }

  /// <summary>True when the process is contained by this job: the fact a containment
  ///     guarantee is asserted against.</summary>
  public static bool IsInJob(nint job, Process process)
  {
    ArgumentNullException.ThrowIfNull(process);
    return job != nint.Zero
        && IsProcessInJob(process.Handle, job, out int contained)
        && contained != 0;
  }

  /// <summary>Closes the job handle; a kill-on-close job reaps any surviving processes.
  ///     A zero handle is a no-op.</summary>
  public static bool Close(nint job) => job != nint.Zero && CloseHandle(job);
}
