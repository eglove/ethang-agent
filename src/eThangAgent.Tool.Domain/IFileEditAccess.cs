using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public interface IFileEditAccess
{
    /// <summary>Literal (non-regex) replacement. When <paramref name="occurrences"/>
    /// is null every occurrence is replaced; otherwise the actual count must equal it.
    /// Refuses binary files. Never creates files.</summary>
    Task<Result<ReplaceOutcome>> ReplaceInFileAsync(
        string path, string oldText, string newText, int? occurrences, CancellationToken ct = default);
}
