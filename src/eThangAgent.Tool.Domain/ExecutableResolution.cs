namespace eThangAgent.ToolDomain;

/// <summary>Finds the file the operating system would start for a command name.
///     <para>A BARE NAME is searched for across the supplied directories in the order given, and
///     within one directory the extensions are tried in PATHEXT order: the first file that exists
///     wins and the search stops. That is the rule the Windows loader applies, and the rule .NET
///     does not apply under <c>UseShellExecute = false</c> - the loader there appends only
///     <c>.exe</c>, so a command published as a <c>.cmd</c> shim cannot be started by name at
///     all.</para>
///     <para>A QUALIFIED NAME - one carrying a directory separator - is resolved against its own
///     directory and the supplied directories are not consulted, because the caller has already
///     said where to look.</para>
///     <para>This answers WHICH FILE a name starts, never whether the file is a runnable tool:
///     presence is <see cref="PathToolDiscovery"/>'s question, and it is answered from the file's
///     own kind.</para></summary>
public static class ExecutableResolution
{
  /// <summary>The extension order used when PATHEXT is absent or empty: the Windows default.
  /// </summary>
  private static readonly string[] DefaultExtensions = [".COM", ".EXE", ".BAT", ".CMD"];

  /// <summary>The extension order <paramref name="pathext"/> declares, in its own order. An
  ///     absent or empty value yields <c>.COM</c>, <c>.EXE</c>, <c>.BAT</c>, <c>.CMD</c>.</summary>
  public static IReadOnlyList<string> Extensions(string? pathext)
  {
    string[] declared = (pathext ?? string.Empty)
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    return declared.Length == 0 ? DefaultExtensions : declared;
  }

  /// <summary>True when <paramref name="name"/> carries a directory separator, so it says where
  ///     to look itself and no directory list applies.</summary>
  public static bool IsQualified(string name)
  {
    ArgumentNullException.ThrowIfNull(name);
    return name.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || name.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
  }

  /// <summary>The absolute path of the first file <paramref name="exe"/> names, or null when no
  ///     directory holds one. Directories are searched in the order supplied; within a directory
  ///     the extensions are tried in <paramref name="pathext"/> order. A qualified name is
  ///     resolved against its own directory and the supplied list is not consulted. Matching is
  ///     case-insensitive because the file system is.</summary>
  public static string? Resolve(string exe, IEnumerable<string> pathDirectories, string? pathext)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(exe);
    ArgumentNullException.ThrowIfNull(pathDirectories);

    IReadOnlyList<string> extensions = Extensions(pathext);
    if (IsQualified(exe))
    {
      return FindIn(Path.GetDirectoryName(exe) ?? string.Empty, Path.GetFileName(exe), extensions);
    }

    foreach (string directory in pathDirectories)
    {
      if (string.IsNullOrWhiteSpace(directory))
      {
        continue;
      }

      if (FindIn(directory.Trim(), exe, extensions) is { } hit)
      {
        return hit;
      }
    }

    return null;
  }

  /// <summary>The absolute path of <paramref name="name"/> plus the first extension in
  ///     <paramref name="extensions"/> that exists in <paramref name="directory"/>, or null.</summary>
  private static string? FindIn(string directory, string name, IReadOnlyList<string> extensions)
  {
    if (directory.Length == 0 || name.Length == 0)
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
}
