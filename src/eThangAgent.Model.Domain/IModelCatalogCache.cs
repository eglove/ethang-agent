using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

/// <summary>One persisted provider catalog snapshot: the expanded model+provider
/// entries and the instant they were fetched. Served stale-while-revalidate so a
/// reopened app shows the model picker instantly and refreshes in the background.</summary>
public sealed record ModelCatalogSnapshot(
    IReadOnlyList<ModelProviderEntry> Entries,
    DateTimeOffset FetchedAt);

/// <summary>Persists a provider's model catalog between sessions. The catalog is one
/// global list per provider (not workspace-scoped), so the key is the provider id.
/// Implementations live behind an ACL (SQLite); the domain knows nothing of the
/// storage engine. Reads and writes flow as <see cref="Result{T}"/> — a cache miss is
/// a named error, and a storage fault never throws.</summary>
public interface IModelCatalogCache
{
  /// <summary>Reads the persisted snapshot for <paramref name="provider"/>, or a
  ///     <c>CatalogCacheMiss</c> failure when none is stored.</summary>
  Task<Result<ModelCatalogSnapshot>> ReadAsync(string provider, CancellationToken ct = default);

  /// <summary>Writes (replaces) the snapshot for <paramref name="provider"/>.</summary>
  Task<Result<bool>> WriteAsync(string provider, ModelCatalogSnapshot snapshot, CancellationToken ct = default);
}
