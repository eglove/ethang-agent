using System.Runtime.InteropServices;

namespace eThangAgent.Roslyn.ACL;

/// <summary>Parent-process lookup for tree sampling (spec D10). Windows only, matching
///     the platform constraint; a failed query returns 0 and the candidate simply does
///     not count toward the tree.</summary>
internal static class ProcessTree
{
  private const int ProcessBasicInformation = 0;
  private const uint QueryLimitedInformation = 0x1000;

  [StructLayout(LayoutKind.Sequential)]
  private struct ProcessBasicInformationData
  {
    public nint _exitStatus;
    public nint _pebBaseAddress;
    public nint _affinityMask;
    public nint _basePriority;
    public nint _uniqueProcessId;
    public nint _inheritedFromUniqueProcessId;
  }

#pragma warning disable SYSLIB1054 // Named decision: LibraryImport needs unsafe blocks; DllImport marshals identically here (the JobObject.cs pattern).
  [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, int processId);
#pragma warning restore SYSLIB1054

#pragma warning disable SYSLIB1054 // Named decision: same rationale as above.
  [DllImport("ntdll.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static extern int NtQueryInformationProcess(nint handle, int infoClass,
      ref ProcessBasicInformationData info, int length, out int returned);
#pragma warning restore SYSLIB1054

#pragma warning disable SYSLIB1054 // Named decision: same rationale as above.
  [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static extern bool CloseHandle(nint handle);
#pragma warning restore SYSLIB1054

  /// <summary>The parent's process id, or 0 when the query fails (the process exited,
  ///     or access was refused — a candidate that cannot be placed in the tree simply
  ///     does not count toward it).</summary>
  public static int ParentOf(int processId)
  {
    // QUERY_LIMITED_INFORMATION rather than QUERY_INFORMATION: the limited right is
    // enough for NtQueryInformationProcess and is granted where the full right is not.
    nint handle = OpenProcess(QueryLimitedInformation, false, processId);
    if (handle == nint.Zero)
    {
      return 0;
    }

    try
    {
      ProcessBasicInformationData info = default;
      int status = NtQueryInformationProcess(handle, ProcessBasicInformation, ref info,
          Marshal.SizeOf<ProcessBasicInformationData>(), out _);
      return status == 0 ? (int)info._inheritedFromUniqueProcessId : 0;
    }
    finally
    {
      _ = CloseHandle(handle);
    }
  }
}
