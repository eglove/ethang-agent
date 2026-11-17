using System.Buffers.Binary;

namespace eThangAgent.ToolDomain;

/// <summary>The tools one PATH directory contributed, in name order.</summary>
public sealed record PathToolDirectory(string Directory, IReadOnlyList<PathTool> Tools);

/// <summary>Every listed tool found across a PATH, grouped by the directory it came from.
///     <para>A group appears only when it contributes at least one listed tool, so an empty
///     or tool-free directory is absent rather than present and empty.
///     <see cref="MissingDirectoryCount"/> counts the entries that could not be enumerated at
///     all: absent, unreadable, or not a valid path.</para></summary>
public sealed record PathToolScan(IReadOnlyList<PathToolDirectory> Directories, int MissingDirectoryCount)
{
  /// <summary>Every listed tool, in directory order and then name order.</summary>
  public IEnumerable<PathTool> Tools => Directories.SelectMany(group => group.Tools);
}

/// <summary>Whether one name runs, and the file that would run.
///     <para><see cref="FullPath"/> is null when nothing on the PATH would run the name, and
///     <see cref="Kind"/> then names nothing. A name whose first match is a GUI program or an
///     unreadable header does not resolve: that file is what the operating system would start,
///     and it is not a tool whose output a caller can read.</para></summary>
public sealed record PathToolResolution(string Name, PathToolKind? Kind, string? FullPath)
{
  /// <summary>True when <see cref="FullPath"/> names a file a caller can run and read.</summary>
  public bool IsResolved => FullPath is not null;
}

/// <summary>Reads a PATH and reports which entries are runnable tools.
///     <para>Inclusion is a property of the file and nothing else: a <c>.cmd</c> or <c>.bat</c> is
///     a script, and an <c>.exe</c> is a console program when its PE optional header names the
///     console subsystem. No name, folder, or size rule narrows the result, so a directory with
///     four hundred genuine console tools reports four hundred tools.</para>
///     <para>Directories are searched in the order supplied, which is PATH order, and the first
///     directory holding a name wins: that is the file the operating system would start.</para></summary>
public static class PathToolDiscovery
{
  /// <summary>The extensions a PATH entry can be run through, in the order a bare name is
  ///     matched within one directory.</summary>
  private static readonly string[] Extensions = [".exe", ".cmd", ".bat"];

  /// <summary>Offset of the field holding the file offset of the PE signature.</summary>
  private const int PeHeaderOffsetField = 0x3C;

  /// <summary>Distance from the PE signature to the optional header's Subsystem field:
  ///     4 signature bytes, a 20-byte COFF header, then 68 bytes into the optional header.</summary>
  private const int SubsystemFromSignature = 4 + 20 + 68;

  private const uint PeSignature = 0x00004550;

  private const ushort ConsoleSubsystem = 3;

  /// <summary>Scans <paramref name="pathDirectories"/> in the order supplied and returns every
  ///     console program and script, grouped by the directory it was found in. A name found in
  ///     more than one directory is reported once, against the earliest directory holding it,
  ///     in the spelling that directory listed. A blank entry contributes nothing; an entry that
  ///     is absent, unreadable, or malformed contributes nothing and increments
  ///     <see cref="PathToolScan.MissingDirectoryCount"/> rather than failing the scan.</summary>
  public static PathToolScan Scan(IEnumerable<string> pathDirectories)
  {
    ArgumentNullException.ThrowIfNull(pathDirectories);

    List<PathToolDirectory> groups = [];
    HashSet<string> seen = [with(StringComparer.OrdinalIgnoreCase)];
    int missing = 0;

    foreach (string directory in pathDirectories)
    {
      if (string.IsNullOrWhiteSpace(directory))
      {
        continue;
      }

      List<PathTool>? tools = ScanDirectory(directory, seen);
      if (tools is null)
      {
        missing++;
      }
      else if (tools.Count > 0)
      {
        groups.Add(new PathToolDirectory(directory, tools));
      }
    }

    return new PathToolScan(groups, missing);
  }

  /// <summary>Resolves each name against <paramref name="pathDirectories"/> in the order
  ///     supplied, returning one result per name in the order given. A name is matched with or
  ///     without a <c>.exe</c>, <c>.cmd</c>, or <c>.bat</c> extension, case-insensitively.
  ///     Within a directory the extensions are tried in <see cref="Extensions"/> order, and the
  ///     first file found decides the answer: a name that resolves is one whose first match is a
  ///     console program or a script, and anything else leaves the name unresolved.</summary>
  public static IReadOnlyList<PathToolResolution> Resolve(IEnumerable<string> toolNames, IEnumerable<string> pathDirectories)
  {
    ArgumentNullException.ThrowIfNull(toolNames);
    ArgumentNullException.ThrowIfNull(pathDirectories);

    string[] directories = [.. pathDirectories];
    List<PathToolResolution> results = [];
    foreach (string name in toolNames)
    {
      ArgumentException.ThrowIfNullOrWhiteSpace(name);
      results.Add(ResolveOne(name, directories));
    }

    return results;
  }

  private static PathToolResolution ResolveOne(string name, string[] directories)
  {
    string bare = BareName(name);
    foreach (string directory in directories)
    {
      if (string.IsNullOrWhiteSpace(directory))
      {
        continue;
      }

      foreach (string extension in Extensions)
      {
        string candidate = Path.Combine(directory, bare + extension);
        if (!File.Exists(candidate))
        {
          continue;
        }

        PathToolKind kind = Classify(candidate, extension);
        return kind is PathToolKind.Unreadable
            ? new PathToolResolution(name, null, null)
            : new PathToolResolution(name, kind, candidate);
      }
    }

    return new PathToolResolution(name, null, null);
  }

  /// <summary>The name without a <c>.exe</c>, <c>.cmd</c>, or <c>.bat</c> extension; any other
  ///     name is returned unchanged.</summary>
  private static string BareName(string name)
  {
    string extension = Path.GetExtension(name);
    return Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
        ? Path.GetFileNameWithoutExtension(name)
        : name;
  }

  /// <summary>The listed tools in one directory, or null when the directory cannot be
  ///     enumerated. Names already in <paramref name="seen"/> are skipped without being read,
  ///     and every name this directory lists is added to it.</summary>
  private static List<PathTool>? ScanDirectory(string directory, HashSet<string> seen)
  {
    List<(string Name, string Extension, string FullPath)> candidates;
    try
    {
      candidates =
      [
        .. Directory.EnumerateFiles(directory)
            .Select(file => (Name: Path.GetFileNameWithoutExtension(file), Extension: Path.GetExtension(file), FullPath: file))
            .Where(candidate => candidate.Name.Length > 0 && Extensions.Contains(candidate.Extension, StringComparer.OrdinalIgnoreCase))
            .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => ExtensionRank(candidate.Extension)),
      ];
    }
    catch (IOException)
    {
      return null;
    }
    catch (UnauthorizedAccessException)
    {
      return null;
    }
    catch (ArgumentException)
    {
      return null;
    }

    List<PathTool> tools = [];
    foreach ((string name, string extension, string fullPath) in candidates)
    {
      if (!seen.Add(name))
      {
        continue;
      }

      PathToolKind kind = Classify(fullPath, extension);
      if (kind is PathToolKind.Unreadable)
      {
        continue;
      }

      tools.Add(new PathTool(name, directory, fullPath, kind));
    }

    return tools;
  }

  /// <summary>A batch file is a script and is never opened as an image; an executable is
  ///     classified by the subsystem its PE optional header names.</summary>
  private static PathToolKind Classify(string fullPath, string extension) =>
      IsScript(extension) ? PathToolKind.Script : ClassifyExecutable(fullPath);

  private static bool IsScript(string extension) =>
      string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase)
      || string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase);

  /// <summary>An executable is a console program only when its subsystem is 3; any other
  ///     subsystem, and an unreadable header, is Unreadable.</summary>
  private static PathToolKind ClassifyExecutable(string fullPath) =>
      ReadSubsystem(fullPath) == ConsoleSubsystem ? PathToolKind.ConsoleProgram : PathToolKind.Unreadable;

  /// <summary>Position of <paramref name="extension"/> in <see cref="Extensions"/>, which is the
  ///     order a bare name is matched in within one directory.</summary>
  private static int ExtensionRank(string extension) =>
      Array.FindIndex(Extensions, candidate => string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase));

  /// <summary>The PE optional header's Subsystem value, or null when the file cannot be read or
  ///     does not carry a PE signature where its header says it does. A file too short for any
  ///     of the offsets reports null instead of throwing.</summary>
  private static ushort? ReadSubsystem(string fullPath)
  {
    try
    {
      using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
      if (stream.Length < PeHeaderOffsetField + sizeof(int))
      {
        return null;
      }

      Span<byte> field = stackalloc byte[sizeof(int)];
      _ = stream.Seek(PeHeaderOffsetField, SeekOrigin.Begin);
      stream.ReadExactly(field);

      int signatureOffset = BinaryPrimitives.ReadInt32LittleEndian(field);
      long subsystemOffset = (long)signatureOffset + SubsystemFromSignature;
      if (signatureOffset < 0 || subsystemOffset + sizeof(ushort) > stream.Length)
      {
        return null;
      }

      Span<byte> signature = stackalloc byte[sizeof(uint)];
      _ = stream.Seek(signatureOffset, SeekOrigin.Begin);
      stream.ReadExactly(signature);
      if (BinaryPrimitives.ReadUInt32LittleEndian(signature) != PeSignature)
      {
        return null;
      }

      Span<byte> subsystem = stackalloc byte[sizeof(ushort)];
      _ = stream.Seek(subsystemOffset, SeekOrigin.Begin);
      stream.ReadExactly(subsystem);
      return BinaryPrimitives.ReadUInt16LittleEndian(subsystem);
    }
    catch (IOException)
    {
      return null;
    }
    catch (UnauthorizedAccessException)
    {
      return null;
    }
  }
}
