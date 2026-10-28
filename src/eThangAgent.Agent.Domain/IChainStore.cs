using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>
/// Persistence port for chains (issue #300). Implementations bind to a workspace at
/// construction — never workspace-parameterized per call. Expected failures flow as
/// <see cref="DomainError"/>s (codes ChainNotFound, VersionConflict) through
/// <see cref="Result{T}"/> — never exceptions.
/// <para>The CAS contract is the plan store's, deliberately: the chain is a sibling of the
/// plan, so a second persistence idiom in the same database would be a second thing to get
/// wrong. A save persists only while the stored version still equals the caller's expected
/// version.</para>
/// </summary>
public interface IChainStore
{
  /// <summary>Persists a new chain; the store assigns the Id and returns the chain at Version 1.</summary>
  Task<Result<Chain>> CreateAsync(Chain draft, CancellationToken ct = default);

  /// <summary>Loads a chain by id; fails with ChainNotFound.</summary>
  Task<Result<Chain>> GetAsync(int id, CancellationToken ct = default);

  /// <summary>Lists chains, optionally filtered by status. A row that cannot be materialized
  /// is SKIPPED and REPORTED in <see cref="ChainCatalog.Corrupt"/> rather than failing the
  /// whole listing (the containment issue #279 established for plans).</summary>
  Task<Result<ChainCatalog>> ListAsync(ChainStatus? status, CancellationToken ct = default);

  /// <summary>Compare-and-swap save: persists the chain only while the stored version still
  /// equals <c>expectedVersion</c>; the store bumps Version on success. Fails with
  /// ChainNotFound or VersionConflict.</summary>
  Task<Result<Chain>> SaveAsync(Chain chain, int expectedVersion, CancellationToken ct = default);
}
