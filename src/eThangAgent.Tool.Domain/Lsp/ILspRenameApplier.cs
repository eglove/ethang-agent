using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Lsp;

/// <summary>The write seam for a computed rename (issue #357): the language server decides
/// what changes, this seam performs it. Expected failures flow as typed errors naming the
/// offending path.</summary>
public interface ILspRenameApplier
{
  /// <summary>Applies every edit as one unit. Either every target is written or none is:
  ///     all paths are validated and all text is computed before the first write, and a
  ///     write that fails partway restores the files already written.</summary>
  Task<Result<LspRenameApplied>> ApplyAsync(IReadOnlyList<LspRenameEdit> edits, CancellationToken ct = default);
}

/// <summary>What a successful rename application changed: the distinct files written and
/// the number of edits applied.</summary>
public sealed record LspRenameApplied(IReadOnlyList<string> FilesChanged, int EditCount);
