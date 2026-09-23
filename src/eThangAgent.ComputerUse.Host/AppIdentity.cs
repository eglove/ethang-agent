
namespace eThangAgent.ComputerUse.Host;

/// <summary>One resolved application identity (task 17): pid, the normalized exe, and
///     the AUMID when the app is packaged. ApplicationFrameHost.exe is NEVER an
///     identity: UWP host windows would all collapse to one name, so the identity is
///     the real hosting app (its AUMID) or nothing.</summary>
public sealed record AppIdentity(int Pid, string ExePath, string ExeName, string? Aumid)
{
  private const string FrameHostExeName = "applicationframehost.exe";

  /// <summary>True when the exe is the UWP frame host. Such a window without a
  ///     resolvable real app AUMID is not reportable as an application identity.</summary>
  public bool IsApplicationFrameHost =>
    ExeName.Equals(FrameHostExeName, StringComparison.OrdinalIgnoreCase) && Aumid is null;

  /// <summary>Resolves the identity for a process: the AUMID comes from the injected
  ///     resolver (production: GetApplicationUserModelId, then the shell-properties
  ///     heuristic); the exe normalizes to a full path plus file name.</summary>
  public static AppIdentity Resolve(int pid, string exePath, Func<string, string?> aumidResolver)
  {
    ArgumentNullException.ThrowIfNull(exePath);
    ArgumentNullException.ThrowIfNull(aumidResolver);
    string fullPath = exePath.Length == 0 ? string.Empty : Path.GetFullPath(exePath);
    string exeName = Path.GetFileName(fullPath);
    string? aumid = null;
    bool isFrameHost = exeName.Equals(FrameHostExeName, StringComparison.OrdinalIgnoreCase);
    if (isFrameHost || IsPackagedPath(fullPath))
    {
      aumid = aumidResolver(pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    return new AppIdentity(pid, fullPath, exeName, string.IsNullOrWhiteSpace(aumid) ? null : aumid);
  }

  private static bool IsPackagedPath(string fullPath) =>
    fullPath.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase);
};
