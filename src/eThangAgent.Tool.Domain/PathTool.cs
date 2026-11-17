namespace eThangAgent.ToolDomain;

/// <summary>How a PATH entry can be run, decided from the file alone — never from its name,
///     the folder holding it, or its size.</summary>
public enum PathToolKind
{
  /// <summary>An <c>.exe</c> whose PE optional header names the console subsystem (3), so it
  ///     reads and writes the caller's standard streams.</summary>
  ConsoleProgram,

  /// <summary>A <c>.cmd</c> or <c>.bat</c> batch file. It is text, so no PE header is read.
  ///     A script spawns through the command processor and runs like any other tool.</summary>
  Script,

  /// <summary>An <c>.exe</c> whose header did not classify it: a GUI program (subsystem 2),
  ///     any other subsystem, or a header that could not be read at all — a truncated file,
  ///     a non-PE image, or a file the operating system refuses to open.</summary>
  Unreadable,
}

/// <summary>One command-line tool found in one PATH directory.
///     <para><see cref="Name"/> is the file name without its extension, in the spelling the
///     directory listing gave it, so a tool is identified the way a caller types it.
///     <see cref="SourceDirectory"/> is the PATH entry the file was found in, which is what a
///     listing groups by. <see cref="FullPath"/> is the file the classification read.</para></summary>
public sealed record PathTool(string Name, string SourceDirectory, string FullPath, PathToolKind Kind);
