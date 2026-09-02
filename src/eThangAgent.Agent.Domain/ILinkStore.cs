using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>One persisted consented-link row: the address a named link dials (W2).</summary>
public sealed record StoredLink(string Name, string Container, string AgentAddress, DateTimeOffset LinkedAt);

/// <summary>Persistence seam for consented agent links, scoped by workspace (W2).
///     Implemented by storage ACLs; the domain never learns SQL. Synchronous by design:
///     the registry's consent decisions are synchronous and hydration happens at
///     construction, so an async seam would force sync-over-async at every gate. Storage
///     faults are Result failures, never exceptions, so consent can surface them.</summary>
public interface ILinkStore
{
  /// <summary>Every link persisted for the workspace, newest first.</summary>
  Result<IReadOnlyList<StoredLink>> List(string workspaceId);

  /// <summary>Inserts or replaces the named link (replace-by-name semantics).</summary>
  Result<string> Upsert(string workspaceId, StoredLink link);

  /// <summary>Deletes the named link; the value reports whether a row was removed.</summary>
  Result<bool> Delete(string workspaceId, string name);
}
