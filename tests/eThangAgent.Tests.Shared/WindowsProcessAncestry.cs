using System.Diagnostics;
using System.Runtime.InteropServices;

namespace eThangAgent.TestSupport;

/// <summary>The Windows ancestry query behind <see cref="IProcessAncestry"/>: walks the
///     live process list, asks each process for its parent id, and keeps the ones whose
///     parent is the requested process.
///     <para>This is the same NtQueryInformationProcess surface the Roslyn ACL's
///     ProcessTree uses, copied rather than referenced: a test-support assembly must not
///     depend on a production ACL, and the struct plus three P/Invokes is small enough
///     that sharing it would cost more than duplicating it (the named decision
///     JobObject.cs already records for the same trade).</para></summary>
public sealed class WindowsProcessAncestry : IProcessAncestry
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

  // Named decision (SYSLIB1054): LibraryImport needs unsafe blocks; DllImport with
  // blittable layouts marshals identically here (the JobObject.cs / ProcessTree.cs pattern).
#pragma warning disable SYSLIB1054
  [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

  [DllImport("ntdll.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static extern int NtQueryInformationProcess(nint handle, int infoClass,
      ref ProcessBasicInformationData info, int length, out int returned);

  [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static extern bool CloseHandle(nint handle);
#pragma warning restore SYSLIB1054

  public IReadOnlyList<LiveProcess> ChildrenOf(int parentProcessId)
  {
    List<LiveProcess> children = [];
    foreach (Process process in Process.GetProcesses())
    {
      using (process)
      {
        int pid;
        string name;
        try
        {
          pid = process.Id;
          name = process.ProcessName;
        }
        catch (InvalidOperationException)
        {
          // The process exited between enumeration and the read: not a candidate.
          continue;
        }

        if (ParentOf(pid) == parentProcessId)
        {
          children.Add(new LiveProcess(pid, name));
        }
      }
    }

    return children;
  }

  /// <summary>The parent's process id, or 0 when the query fails (the process exited, or
  ///     access was refused - a candidate that cannot be placed in the tree simply does not
  ///     count as a child, which is the safe direction: it never invents a leak).</summary>
  private static int ParentOf(int processId)
  {
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
