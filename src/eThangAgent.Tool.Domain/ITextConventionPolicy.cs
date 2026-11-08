namespace eThangAgent.ToolDomain;

/// <summary>Resolves the textual conventions a write should land in: WHICH line ending, and
/// whether the text carries a final newline. The seam exists so convention discovery
/// (editorconfig, gitattributes, workspace majority) is a replaceable capability rather than a
/// utility welded into the file ACL.
/// Resolution order at a write: the existing file's dominant line ending; else the project
/// convention this policy reports; else LF. The final-newline setting is the project's alone —
/// an existing file's trailing newline is not a convention a write must preserve.</summary>
public interface ITextConventionPolicy
{
  /// <summary>The convention the project wants for <paramref name="filePath"/>. The path is
  /// absolute and may name a file that does not exist yet, so the file's own directory is what
  /// gets examined. Never null: when nothing declares an ending the session default (LF) is the
  /// answer, and when nothing declares a final newline the text is written verbatim in that
  /// respect.</summary>
  TextConvention Resolve(string filePath);
}
