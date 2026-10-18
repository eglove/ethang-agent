using System.Diagnostics;
using System.Runtime.InteropServices;

namespace eThangAgent.Roslyn.ACL;

/// <summary>Windows Job Object containment for one spawned process tree (the
///     2026-10-17 incident: orphaned testhosts and build servers, ~3 GB and ~7M
///     handles, survived their child runs for hours). The spawned process is
///     assigned to a kill-on-close job, so the exec call returning - or the harness
///     process dying (crash or exit) - closes the job handle and the kernel reaps
///     the whole tree. Best-effort: a job failure degrades to the explicit-kill
///     path. Copied from the MCP ACL's JobObject (itself extracted from the
///     ComputerUse broker's BrokerSupervisor.JobObject) - the Roslyn ACL cannot
///     reference the MCP ACL, and the struct/P-Invoke surface is small enough
///     that a shared infrastructure project would outgrow its value.</summary>
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
