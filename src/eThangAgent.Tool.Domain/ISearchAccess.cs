using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public interface ISearchAccess
{
    /// <summary>Bounded content search over workspace text files. Skips .git contents and
    /// binary files. Returns at most <paramref name="maxResults"/> matches; when further
    /// files remained unscanned, <c>Truncated</c> is true and every scanned match counts.
    /// Literal mode compares ordinally; regex mode uses a 2-second match timeout.</summary>
    Task<Result<FileSearch>> SearchFilesAsync(
        string rootPath, string pattern, bool regex, string? glob,
        int maxResults, int contextLines, CancellationToken ct = default);
}
