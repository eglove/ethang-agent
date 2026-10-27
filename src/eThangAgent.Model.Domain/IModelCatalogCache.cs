using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

/// <summary>One persisted provider catalog snapshot: the expanded model+provider
/// entries and the instant they were fetched. Served stale-while-revalidate so a
/// reopened app shows the model picker instantly and refreshes in the background.
///     <para><c>ShapeVersion</c> is the ENTRY SHAPE the snapshot was written with, and it is
///     load-bearing rather than bookkeeping (issue #296 follow-up). A snapshot written before
///     endpoint variants existed carries no <c>Variant</c> on any entry, so every endpoint of
///     a provider deserializes with the unspecified variant and collapses back onto the same
///     pair key - the picker would show one collapsed row for the whole stale window, which is
///     exactly the defect variants were added to fix. A snapshot whose shape version is older
///     than <see cref="CurrentShapeVersion"/> is therefore treated as a MISS and refetched,
///     never served as though it were current.</para>
///     <para>Version 0 is the legacy array payload; version 1 is the envelope that carries
///     variant identity. The number is bumped whenever a member is added that a consumer
///     depends on for CORRECTNESS (not merely for display), because the alternative is a
///     silently wrong catalog for as long as the TTL lasts.</para></summary>
public sealed record ModelCatalogSnapshot(
    IReadOnlyList<ModelProviderEntry> Entries,
    DateTimeOffset FetchedAt,
    int ShapeVersion = ModelCatalogSnapshot.LegacyShapeVersion)
{
  /// <summary>The shape a snapshot written before variants existed has.</summary>
  public const int LegacyShapeVersion = 0;

  /// <summary>The shape this build writes and requires.</summary>
  public const int CurrentShapeVersion = 1;

  /// <summary>True when this snapshot carries the entry shape this build depends on.
  ///     A legacy snapshot is not corrupt - it is simply not detailed enough to serve, and
  ///     the caller refetches instead of rendering a collapsed list.</summary>
  public bool HasCurrentShape => ShapeVersion >= CurrentShapeVersion;
}

/// <summary>Persists a provider's model catalog between sessions. The catalog is one
/// global list per provider source (not workspace-scoped). The key is an opaque
/// source identity supplied by the provider ACL, never just a provider id.
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
