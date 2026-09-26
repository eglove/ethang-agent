using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>File-name search over a directory root — the seam behind find_files. The
///     implementation owns the exclusion rules (build output, VCS internals) and the
///     result cap; the tool owns validation and the output contract.</summary>
public interface IFileSearchAccess
{
  /// <summary>Returns absolute file paths under <paramref name="root"/> whose names match
  ///     <paramref name="searchPattern"/> (Win32 glob: * and ?). Recurses when
  ///     <paramref name="recurse"/>; the implementation's exclusions and cap apply.</summary>
  Task<Result<IReadOnlyList<string>>> EnumerateFilesAsync(string root, string searchPattern, bool recurse, CancellationToken ct = default);
}
