using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Sessions;

/// <summary>One resumable root session as the Sessions catalog lists it: the record's
///     discovery binding (workspace + provider) plus its lifecycle timestamps. Carries no
///     conversation content — resume hydrates from the transcript store by id. (Distinct
///     from the Agent Domain's memory-tool <see cref="SessionSummary"/>, which describes
///     recall corpora.)</summary>
public sealed record SessionCatalogEntry(
    AgentId Id,
    string WorkspaceId,
    string Provider,
    AgentStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

/// <summary>Read-side query listing every resumable root session, newest first. Only
///     depth-0 records WITH a workspace + provider binding are listed — spawned children
///     are not sessions, and rows persisted before workspace binding cannot be resumed
///     (their workspace is unknown). A row bound to a provider this build no longer knows
///     is likewise not resumable (resume would fail UnknownProvider), so the optional
///     <c>isResumableProvider</c> gate omits it — wired at the composition root, since
///     provider identity is a Composition concern the domain must not know. Deliberately
///     transcript-free: listing loads record rows only, never conversation content.</summary>
public sealed class SessionCatalogQueryHandler(IAgentStore store, Func<string, bool>? isResumableProvider = null)
{
  private readonly IAgentStore _store = store ?? throw new ArgumentNullException(nameof(store));
  private readonly Func<string, bool> _isResumableProvider = isResumableProvider ?? (_ => true);

  public async Task<Result<IReadOnlyList<SessionCatalogEntry>>> ListAsync(CancellationToken ct = default)
  {
    Result<IReadOnlyList<AgentRecord>> records = await _store.ListAllAsync(ct).ConfigureAwait(false);
    if (!records.IsSuccess)
    {
      return Result.Failure<IReadOnlyList<SessionCatalogEntry>>(records.Error);
    }

    // ListAllAsync returns rows in creation order; the catalog is newest first.
    List<SessionCatalogEntry> entries = [.. records.Value
        .Where(record => record.Depth == 0
            && record.WorkspaceId is not null
            && record.Provider is not null
            && _isResumableProvider(record.Provider))
        .OrderByDescending(record => record.CreatedAt)
        .Select(record => new SessionCatalogEntry(
            record.Id,
            record.WorkspaceId!,
            record.Provider!,
            record.Status,
            record.CreatedAt,
            record.CompletedAt))];
    return Result.Success<IReadOnlyList<SessionCatalogEntry>>(entries);
  }
}
