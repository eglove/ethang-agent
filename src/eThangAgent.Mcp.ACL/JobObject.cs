using System.Diagnostics;
using System.Runtime.InteropServices;

namespace eThangAgent.Mcp.ACL;

/// <summary>Windows Job Object containment for one stdio MCP server process (issue
///     #105): the server is assigned to a kill-on-close job, so the harness dying
///     (crash or exit) closes the job handle and the kernel reaps the server - a hung
///     server can never outlive its session. Best-effort: a job failure degrades to
///     explicit-kill disposal. The ComputerUse broker's precedent
///     (BrokerSupervisor.JobObject), extracted for the MCP ACL.</summary>
internal static class JobObject
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

  /// <summary>Creates an UNNAMED kill-on-close job (the broker's ruling): per-spawn
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
