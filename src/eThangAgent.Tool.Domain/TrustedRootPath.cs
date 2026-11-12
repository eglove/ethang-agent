namespace eThangAgent.ToolDomain;

/// <summary>Canonical form and containment for the external roots a session may reach.
///     A root is fully qualified with any trailing separators removed, so comparisons never
///     differ by separator or casing; a drive root ('C:\') keeps its separator or it would
///     stop naming the drive root. Containment is segment-aware, so a sibling directory whose
///     name merely shares a prefix with a root is not inside it.</summary>
public static class TrustedRootPath
{
  /// <summary>Canonical form of a root or of a path being compared against one.</summary>
  public static string Normalize(string path)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    return full.Length == 2 && full[1] == ':' ? full + Path.DirectorySeparatorChar : full;
  }

  /// <summary>True when <paramref name="candidate"/> is <paramref name="root"/> or lies
  ///     beneath it, compared case-insensitively and segment by segment. Both arguments are
  ///     expected to be in <see cref="Normalize"/>d form.</summary>
  public static bool Contains(string root, string candidate)
  {
    ArgumentNullException.ThrowIfNull(root);
    ArgumentNullException.ThrowIfNull(candidate);
    return string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
  }
}
