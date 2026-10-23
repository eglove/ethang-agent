using eThangAgent.ToolDomain;

// Named decision (CA1849): convention discovery reads a handful of small text files
// from the local disk; sync APIs inside the sync Resolve keep the code simple without
// meaningful thread blocking.
#pragma warning disable CA1849 // Call async methods when in an async method
namespace eThangAgent.FileSystem.ACL;

/// <summary>Resolves a write's line-ending convention from the project's own
///     declarations, in a fixed order: the nearest <c>.editorconfig</c> chain's
///     <c>end_of_line</c> (section globs via <see cref="SectionGlob"/>, later matching
///     sections winning within one file, nearer files winning across the chain), then
///     the nearest <c>.gitattributes</c> <c>eol=</c>, then the workspace's existing
///     majority, then LF. An unrecognised value is ignored — a typo in a config file
///     is information, never a failed write — so resolution continues down the order.
///     <para><c>end_of_line = unset</c> WITHDRAWS the setting: it stops the search in
///     the file it appears in, exactly as the editorconfig spec defines 'unset', so a
///     broader section above it cannot silently keep voting.</para></summary>
public sealed class EditorConfigLineEndingPolicy(string workspaceRoot) : ILineEndingPolicy
{
  private const string EditorConfigName = ".editorconfig";
  private const string GitAttributesName = ".gitattributes";
  private const string EndOfLineKey = "end_of_line";
  private const string EolKey = "eol";

  /// <summary>Bound on how far the chain walk climbs, so a run that somehow escapes
  ///     the workspace still terminates.</summary>
  private const int MaxClimbDepth = 32;

  private readonly string _workspaceRoot = NormalizeRoot(workspaceRoot);

  public LineEnding Resolve(string filePath)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
    string full = Path.GetFullPath(filePath);
    string directory = Path.GetDirectoryName(full) ?? _workspaceRoot;

    return FromEditorConfigChain(full, directory)
        ?? FromGitAttributes(full, directory)
        ?? MajorityOfWorkspace()
        ?? LineEnding.Lf;
  }

  /// <summary>Walks from the file's own directory toward the root, applying each
  ///     <c>.editorconfig</c> found. The NEAREST file that decides wins, so the walk
  ///     stops there; a file claiming <c>root = true</c> ends the chain regardless.</summary>
  private static LineEnding? FromEditorConfigChain(string filePath, string directory)
  {
    DirectoryInfo? current = new(directory);
    for (int depth = 0; depth < MaxClimbDepth && current is not null; depth++)
    {
      EditorConfigDocument? document = LoadEditorConfig(Path.Combine(current.FullName, EditorConfigName));
      if (document is not null)
      {
        LineEnding? decided = DecideFor(document, current.FullName, filePath);
        if (decided is not null)
        {
          return decided;
        }

        if (document.ClaimsRoot)
        {
          return null; // owns the chain but does not declare the setting
        }
      }

      current = current.Parent;
    }

    return null;
  }

  /// <summary>Applies one .editorconfig's sections to a file: the LAST matching section
  ///     that names <c>end_of_line</c> is the answer. A matching section declaring
  ///     'unset' withdraws the setting and is itself the final word in this file —
  ///     returning a sentinel-free null here lets the caller keep walking outward.
  ///     </summary>
  private static LineEnding? DecideFor(EditorConfigDocument document, string configDirectory, string filePath)
  {
    string relative = RelativeFor(configDirectory, filePath);
    LineEnding? decided = null;
    foreach (EditorConfigSection section in document.Sections)
    {
      bool applies = section.Glob is null || SectionGlob.Matches(section.Glob, relative);
      if (!applies || !section.TryGet(EndOfLineKey, out string value))
      {
        continue;
      }

      if (TryParseEndOfLine(value, out LineEnding parsed))
      {
        decided = parsed;
      }
      else if (string.Equals(value, "unset", StringComparison.OrdinalIgnoreCase))
      {
        return null; // withdrawn: a broader section must not keep voting
      }
    }

    return decided;
  }

  private static LineEnding? FromGitAttributes(string filePath, string directory)
  {
    DirectoryInfo? current = new(directory);
    for (int depth = 0; depth < MaxClimbDepth && current is not null; depth++)
    {
      string candidate = Path.Combine(current.FullName, GitAttributesName);
      if (File.Exists(candidate))
      {
        LineEnding? found = ReadGitAttributes(candidate, current.FullName, filePath);
        if (found is not null)
        {
          return found;
        }
      }

      current = current.Parent;
    }

    return null;
  }

  /// <summary>Later lines win in gitattributes, so the LAST applicable <c>eol=</c> in the
  ///     nearest file is the answer.</summary>
  private static LineEnding? ReadGitAttributes(string path, string configDirectory, string filePath)
  {
    string relative = RelativeFor(configDirectory, filePath);
    LineEnding? found = null;
    foreach (string raw in ReadAllLines(path))
    {
      string line = raw.Trim();
      if (line.Length == 0 || line[0] == '#')
      {
        continue;
      }

      string[] tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
      if (tokens.Length < 2 || !SectionGlob.Matches(tokens[0], relative))
      {
        continue;
      }

      foreach (string attribute in tokens.Skip(1))
      {
        if (attribute.StartsWith(EolKey + "=", StringComparison.OrdinalIgnoreCase)
            && TryParseEndOfLine(attribute[(EolKey.Length + 1)..], out LineEnding parsed))
        {
          found = parsed;
        }
      }
    }

    return found;
  }

  /// <summary>The workspace's own existing majority, sampled within a fixed budget (see
  ///     <see cref="WorkspaceTextFiles.SampleMajority"/>): this runs on the write path, so
  ///     a large tree costs a bounded sample rather than a full scan. A tie or no
  ///     evidence at all is null, and then LF is the answer.</summary>
  private LineEnding? MajorityOfWorkspace() => WorkspaceTextFiles.SampleMajority(_workspaceRoot).Dominant;

  /// <summary>Path of <paramref name="filePath"/> relative to
  ///     <paramref name="baseDirectory"/> with forward slashes: section globs use
  ///     forward slashes on every platform.</summary>
  private static string RelativeFor(string baseDirectory, string filePath) =>
      Path.GetRelativePath(baseDirectory, filePath).Replace('\\', '/');

  private static EditorConfigDocument? LoadEditorConfig(string path) =>
      File.Exists(path) ? EditorConfigDocument.Parse(ReadAllLines(path)) : null;

  private static string[] ReadAllLines(string path)
  {
    try
    {
      return File.ReadAllLines(path);
    }
    catch (IOException)
    {
      return []; // unreadable config declares nothing
    }
    catch (UnauthorizedAccessException)
    {
      return [];
    }
  }

  /// <summary>The value vocabulary this tool can produce: lf and crlf. A bare 'cr' names
  ///     a convention nothing here emits, and anything else is a typo — both decline
  ///     rather than guessing.</summary>
  private static bool TryParseEndOfLine(string value, out LineEnding ending)
  {
    bool recognized;
    switch (value.Trim().ToUpperInvariant())
    {
      case "LF":
        ending = LineEnding.Lf;
        recognized = true;
        break;
      case "CRLF":
        ending = LineEnding.CrLf;
        recognized = true;
        break;
      default:
        ending = LineEnding.Lf;
        recognized = false;
        break;
    }

    return recognized;
  }

  private static string NormalizeRoot(string root) =>
      string.IsNullOrWhiteSpace(root)
          ? throw new ArgumentException("Workspace root must be a non-empty path.", nameof(root))
          : Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
