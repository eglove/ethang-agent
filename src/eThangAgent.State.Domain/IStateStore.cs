using eThangAgent.SharedKernel;

namespace eThangAgent.StateDomain;

public interface IStateStore
{
  Task<StateKeyValue?> GetKeyAsync(
      string workspaceId, string ns, string name, CancellationToken ct = default);

  Task<IReadOnlyList<StateKeyValue>> ListKeysAsync(
      string workspaceId, string? ns, CancellationToken ct = default);

  /// <summary>Atomic CAS write. Returns the new row, or null when expectedVersion
  ///     was supplied and did not match (fail-closed conflict).</summary>
  Task<StateKeyValue?> SetKeyCasAsync(
      string workspaceId, string ns, string name, string value,
      int? expectedVersion, CancellationToken ct = default);

  /// <summary>Deletes every key whose namespace starts with the given prefix
  ///     within a workspace. Returns the number of rows removed.</summary>
  Task<int> DeleteNamespacePrefixAsync(
      string workspaceId, string nsPrefix, CancellationToken ct = default);

  /// <summary>Full-text search over state keys in a workspace. Invalid FTS
  ///     queries fail with InvalidQuery rather than throwing.</summary>
  Task<Result<IReadOnlyList<StateSearchHit>>> SearchKeysAsync(
      string workspaceId, string query, int limit, CancellationToken ct = default);

  /// <summary>Atomic CAS delete. Returns false on conflict or missing key.</summary>
  Task<bool> DeleteKeyCasAsync(
      string workspaceId, string ns, string name,
      int? expectedVersion, CancellationToken ct = default);

  Task<TransitionRecord> InsertTransitionAsync(
      string workspaceId, TransitionRecord transition, CancellationToken ct = default);

  /// <summary>Empty ids selects every pending transition.</summary>
  Task<IReadOnlyList<TransitionRecord>> GetTransitionsAsync(
      string workspaceId, IReadOnlyList<string> transitionIds, CancellationToken ct = default);

  Task SetTransitionStatusAsync(
      string workspaceId, string transitionId, string status, CancellationToken ct = default);

  Task AppendEventAsync(
      string workspaceId, string kind, string payloadJson, CancellationToken ct = default);

  Task<IReadOnlyList<StateEvent>> GetEventsAsync(
      string workspaceId, int limit, CancellationToken ct = default);
}
