namespace eThangAgent.ToolDomain;

/// <summary>Resolves WHICH line-ending convention a write should land in. The seam
/// exists so convention discovery (editorconfig, gitattributes, workspace majority)
/// is a replaceable capability rather than a utility welded into the file ACL.
/// Resolution order at a write: the existing file's dominant convention; else the
/// project convention this policy reports; else LF.</summary>
public interface ILineEndingPolicy
{
  /// <summary>The convention the project wants for <paramref name="filePath"/>. The
  /// path is absolute and may name a file that does not exist yet, so the file's own
  /// directory is what gets examined. Never null: when nothing declares a convention
  /// the session default (LF) is the answer.</summary>
  LineEnding Resolve(string filePath);
}
