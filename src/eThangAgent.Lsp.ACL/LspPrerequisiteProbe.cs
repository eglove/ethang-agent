namespace eThangAgent.Lsp.ACL;

/// <summary>Whether a toolchain executable is on this machine.</summary>
public enum PrerequisiteStatus
{
  /// <summary>The executable was found and its absolute path is known.</summary>
  Present,

  /// <summary>The executable is not on this machine.</summary>
  Absent,

  /// <summary>The search could not be performed, so nothing is claimed either way.</summary>
  Unknown,
}

/// <summary>A prerequisite lookup: the status, and the absolute path when it is present.</summary>
public sealed record PrerequisiteProbeResult(PrerequisiteStatus Status, string? ExecutablePath)
{
  /// <summary>A found executable.</summary>
  public static PrerequisiteProbeResult Found(string path) => new(PrerequisiteStatus.Present, path);

  /// <summary>An executable that is not on this machine.</summary>
  public static PrerequisiteProbeResult Missing => new(PrerequisiteStatus.Absent, null);

  /// <summary>A lookup that could not be performed.</summary>
  public static PrerequisiteProbeResult Unsearched => new(PrerequisiteStatus.Unknown, null);
}

/// <summary>Finds a toolchain executable on this machine, so an install can decide whether
///     it has anything to run before it runs it. The search is a file probe, never a
///     spawned process.</summary>
public sealed class LspPrerequisiteProbe
{
  private readonly Func<string, PrerequisiteProbeResult> _locate;

  public LspPrerequisiteProbe(Func<string, PrerequisiteProbeResult> locate)
      => _locate = locate ?? throw new ArgumentNullException(nameof(locate));

  /// <summary>The default probe: PATH order, then PATHEXT order, first hit wins.</summary>
  public static LspPrerequisiteProbe CreateDefault() => new(LocateOnPath);

  /// <summary>Looks up <paramref name="executable"/>, returning its absolute path when it
  ///     is present.</summary>
  public PrerequisiteProbeResult Probe(string executable)
  {
    if (string.IsNullOrWhiteSpace(executable))
    {
      return PrerequisiteProbeResult.Unsearched;
    }

    return _locate(executable);
  }

  private static PrerequisiteProbeResult LocateOnPath(string executable)
  {
    string[] extensions = ExecutableExtensions();
    if (executable.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || executable.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
    {
      string? direct = FirstExisting(
          Path.GetDirectoryName(executable) ?? string.Empty, Path.GetFileName(executable), extensions);
      return direct is null ? PrerequisiteProbeResult.Missing : PrerequisiteProbeResult.Found(direct);
    }

    string? path = Environment.GetEnvironmentVariable("PATH");
    if (string.IsNullOrWhiteSpace(path))
    {
      return PrerequisiteProbeResult.Unsearched;
    }

    foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
    {
      string? hit = FirstExisting(directory.Trim(), executable, extensions);
      if (hit is not null)
      {
        return PrerequisiteProbeResult.Found(hit);
      }
    }

    return PrerequisiteProbeResult.Missing;
  }

  private static string? FirstExisting(string directory, string name, string[] extensions)
  {
    if (directory.Length == 0)
    {
      return null;
    }

    foreach (string extension in extensions)
    {
      string candidate = Path.Combine(directory, name + extension);
      if (File.Exists(candidate))
      {
        return Path.GetFullPath(candidate);
      }
    }

    return null;
  }

  private static string[] ExecutableExtensions()
  {
    string pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? string.Empty;
    string[] declared = pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    return declared.Length == 0 ? [".COM", ".EXE", ".BAT", ".CMD"] : declared;
  }
}
