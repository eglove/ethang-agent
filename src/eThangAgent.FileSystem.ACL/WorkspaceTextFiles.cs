using eThangAgent.ToolDomain;

// Named decision (CA1849): the workspace-majority walk is local-disk enumeration and
// small-file reads; sync APIs keep the code simple without meaningful thread blocking.
#pragma warning disable CA1849 // Call async methods when in an async method
namespace eThangAgent.FileSystem.ACL;

/// <summary>The workspace's existing line-ending evidence: which files are sampled and
///     what each one's dominant convention is. BOUNDED BY CONSTRUCTION — this runs on
///     the write path, so it may never walk an arbitrarily large tree:</summary>
/// <list type="bullet">
///     <item>at most <see cref="MaxSampledFiles"/> files are examined;</item>
///     <item>the walk STOPS as soon as the leading convention cannot be overtaken by
///     the remaining sample budget (a result that cannot change is not worth the IO);</item>
///     <item>a conservative file-size cap skips anything too large to be useful evidence.</item>
/// </list>
/// <para>The rule is a HEURISTIC for projects that declare no convention at all; the
/// declarations (.editorconfig, .gitattributes) are checked first and are never
/// affected by these bounds.</para>
internal static class WorkspaceTextFiles
{
  /// <summary>Upper bound on files examined for the majority heuristic. Chosen to be
  ///     decisive on real projects while costing a fixed, small amount of IO.</summary>
  public const int MaxSampledFiles = 256;

  /// <summary>Files larger than this are skipped: a vendored blob or a lockfile is not
  ///     useful evidence about the project's own convention.</summary>
  private const long MaxSampledBytes = 1024 * 1024;

  /// <summary>Directory names never sampled (VCS internals, build output, dependency
  ///     trees, worktrees, tool caches) — vendored trees must not outvote the project.
  ///     A superset of the file-search exclusions, plus tool-cache directories that
  ///     carry generated files.</summary>
  private static readonly string[] ExcludedDirectories =
  [
    ".git", ".svn", ".hg",
    "bin", "obj", "node_modules", "packages",
    ".worktrees", ".venv", "venv", "env", "__pycache__",
    ".idea", ".vs", ".vscode", ".next", ".nuget", "dist", "build", "coverage",
  ];

  /// <summary>What a bounded sample found: how many files were examined and which
  ///     convention leads (null when nothing leads).</summary>
  public readonly record struct Sample(int Examined, LineEnding? Dominant);

  /// <summary>Samples the tree under <paramref name="root"/> for the majority
  ///     convention, stopping at <see cref="MaxSampledFiles"/> or as soon as the
  ///     leading count cannot be overtaken.</summary>
  public static Sample SampleMajority(string root)
  {
    int crTerminated = 0;
    int lfTerminated = 0;
    int examined = 0;

    foreach (string file in Enumerate(root))
    {
      if (examined >= MaxSampledFiles)
      {
        break;
      }

      switch (DominantOf(file))
      {
        case LineEnding.CrLf:
          crTerminated++;
          examined++;
          break;
        case LineEnding.Lf:
          lfTerminated++;
          examined++;
          break;
        default:
          break; // a file with no dominant convention is not evidence
      }

      if (CannotBeOvertaken(crTerminated, lfTerminated, examined))
      {
        break;
      }
    }

    return new Sample(examined, Majority(crTerminated, lfTerminated));
  }

  /// <summary>True when the leading count is already larger than everything the
  ///     remaining sample budget could add to the trailing side — the outcome is
  ///     settled, so the walk can stop.</summary>
  private static bool CannotBeOvertaken(int crTerminated, int lfTerminated, int examined)
  {
    int remaining = MaxSampledFiles - examined;
    return Math.Abs(crTerminated - lfTerminated) > remaining;
  }

  private static LineEnding? Majority(int crTerminated, int lfTerminated) =>
      (crTerminated, lfTerminated) switch
      {
        (0, 0) => null,
        var (cr, lf) when cr == lf => null,
        var (cr, lf) when cr > lf => LineEnding.CrLf,
        _ => LineEnding.Lf,
      };

  /// <summary>Text files under <paramref name="root"/>, outermost first. Unreadable
  ///     directories end their own subtree instead of failing the walk.</summary>
  private static IEnumerable<string> Enumerate(string root)
  {
    EnumerationOptions options = new() { IgnoreInaccessible = true, RecurseSubdirectories = false };
    foreach (string file in SafeFiles(root, options).Where(IsSamplable))
    {
      yield return file;
    }

    foreach (string child in SafeDirectories(root, options))
    {
      if (ExcludedDirectories.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase))
      {
        continue;
      }

      foreach (string file in Enumerate(child))
      {
        yield return file;
      }
    }
  }

  /// <summary>A file worth sampling: text, and small enough to be project content
  ///     rather than a generated blob.</summary>
  private static bool IsSamplable(string path)
  {
    try
    {
      FileInfo info = new(path);
      return info.Length <= MaxSampledBytes && !LooksBinary(path);
    }
    catch (IOException)
    {
      return false;
    }
    catch (UnauthorizedAccessException)
    {
      return false;
    }
  }

  /// <summary>The file's own dominant convention, or null when it has none (binary,
  ///     unreadable, mixed without a majority, or no terminator at all).</summary>
  private static LineEnding? DominantOf(string path)
  {
    string text;
    try
    {
      text = File.ReadAllText(path);
    }
    catch (IOException)
    {
      return null;
    }
    catch (UnauthorizedAccessException)
    {
      return null;
    }

    return LineEndings.DetectDominant(text);
  }

  private static IEnumerable<string> SafeFiles(string directory, EnumerationOptions options)
  {
    try
    {
      return Directory.EnumerateFiles(directory, "*", options);
    }
    catch (IOException)
    {
      return [];
    }
    catch (UnauthorizedAccessException)
    {
      return [];
    }
  }

  private static IEnumerable<string> SafeDirectories(string directory, EnumerationOptions options)
  {
    try
    {
      return Directory.EnumerateDirectories(directory, "*", options);
    }
    catch (IOException)
    {
      return [];
    }
    catch (UnauthorizedAccessException)
    {
      return [];
    }
  }

  /// <summary>A NUL byte in the first block means the file is not text, so it must not
  ///     vote on the workspace's line-ending convention.</summary>
  private static bool LooksBinary(string path)
  {
    try
    {
      using FileStream fs = File.OpenRead(path);
      byte[] buffer = new byte[512];
      int read = fs.Read(buffer, 0, buffer.Length);
      return Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
    }
    catch (IOException)
    {
      return true; // unreadable: unusable evidence
    }
    catch (UnauthorizedAccessException)
    {
      return true;
    }
  }
}
