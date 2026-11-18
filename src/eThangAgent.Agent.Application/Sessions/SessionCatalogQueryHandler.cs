using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Agent.Application.Sessions;

/// <summary>One resumable root session as the Sessions catalog lists it: the record's
///     discovery binding (workspace + provider) plus its lifecycle timestamps. Carries no
///     conversation content — resume hydrates from the transcript store by id.</summary>
public sealed record SessionCatalogEntry(
    AgentId Id,
    string WorkspaceId,
    string Provider,
    AgentStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

/// <summary>Read-side query listing ONE workspace's resumable sessions, newest first. A
///     record WITH a workspace + provider binding is a session: an agent is a session, so
///     there is no other kind of row to exclude (issue #334 removed the depth test that used
///     to separate a root from its children). Rows persisted before workspace binding cannot
///     be resumed (their workspace is unknown). A row bound to a provider this build no longer
///     knows is likewise not resumable (resume would fail UnknownProvider), so the optional
///     <c>isResumableProvider</c> gate omits it — wired at the composition root, since provider
///     identity is a Composition concern the domain must not know. Deliberately transcript-free:
///     listing loads record rows only, never conversation content.
///     <para>THE SCOPE IS THE QUERY, NOT A FILTER OVER THE RESULT: a window can resume only a
///     session of its own workspace, so the store read is asked for that workspace and another
///     workspace's rows are never loaded. There is no unscoped listing to fall back to — a
///     handler that was bound to nothing and is asked for nothing refuses rather than listing
///     every workspace.</para></summary>
public sealed class SessionCatalogQueryHandler(
    IAgentStore store,
    Func<string, bool>? isResumableProvider = null,
    string? workspaceId = null)
{
  private readonly IAgentStore _store = store ?? throw new ArgumentNullException(nameof(store));
  private readonly Func<string, bool> _isResumableProvider = isResumableProvider ?? (_ => true);

  /// <summary>The workspace this handler lists when a call states none, normalized, or empty
  ///     when it was bound to nothing.</summary>
  public string BoundWorkspaceId { get; } =
      workspaceId is null ? string.Empty : WorkspaceIdentity.Normalize(workspaceId);

  /// <summary>This handler bound to one workspace, so a caller that lists that workspace
  ///     repeatedly states it once. The provider gate is carried over unchanged.</summary>
  /// <param name="workspaceId">The workspace the returned handler lists.</param>
  public SessionCatalogQueryHandler ForWorkspace(string workspaceId)
      => new(_store, _isResumableProvider, workspaceId);

  /// <summary>The workspace's resumable sessions, newest first. A failure whose code is
  ///     <c>InvalidWorkspace</c> when neither the argument nor the handler's binding names a
  ///     workspace.</summary>
  /// <param name="workspaceId">The workspace to list, or null to use the handler's binding.</param>
  /// <param name="ct">Cancellation for the read.</param>
  public async Task<Result<IReadOnlyList<SessionCatalogEntry>>> ListAsync(string? workspaceId = null,
      CancellationToken ct = default)
  {
    string normalized = WorkspaceIdentity.Normalize(workspaceId ?? BoundWorkspaceId);
    if (normalized.Length == 0)
    {
      return Result.Failure<IReadOnlyList<SessionCatalogEntry>>(new DomainError("InvalidWorkspace",
          "a session catalog is scoped to one workspace, and no workspace was stated."));
    }

    Result<IReadOnlyList<AgentRecord>> records = await _store
        .ListByWorkspaceAsync(normalized, ct).ConfigureAwait(false);
    if (!records.IsSuccess)
    {
      return Result.Failure<IReadOnlyList<SessionCatalogEntry>>(records.Error);
    }

    // The store returns rows in creation order; the catalog is newest first.
    List<SessionCatalogEntry> entries = [.. records.Value
        .Where(record => record.WorkspaceId is not null
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
