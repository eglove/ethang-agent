using eThangAgent.SharedKernel;

namespace eThangAgent.StateDomain;

public interface IStateService
{
    Task<Result<string>> GetAsync(string key, CancellationToken ct = default);
    Task<Result<StateKeyValue>> SetAsync(string key, string value, int? expectedVersion, CancellationToken ct = default);
    Task<Result<string>> DeleteAsync(string key, int? expectedVersion, CancellationToken ct = default);
    Task<Result<IReadOnlyList<string>>> ListAsync(string? ns, CancellationToken ct = default);
    /// <summary>Full-text search over workspace state. Query required; limit
    ///     clamped to 1..100 by validation (InvalidLimit outside).</summary>
    Task<Result<IReadOnlyList<StateSearchHit>>> SearchAsync(
        string query, int limit, CancellationToken ct = default);
    Task<Result<string>> TransitionAsync(string from, string to, string summary,
        IReadOnlyList<string> evidence, CancellationToken ct = default);
    Task<CertificationReport> VerifyAsync(IReadOnlyList<string>? ids, CancellationToken ct = default);
    Task<CertificationReport> CheckGoalAsync(CancellationToken ct = default);
    Task<Result<IReadOnlyList<string>>> HistoryAsync(int limit, CancellationToken ct = default);
}
