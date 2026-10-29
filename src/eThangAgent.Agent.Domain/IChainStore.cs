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
  ///     is SKIPPED and REPORTED in <see cref="ChainCatalog.Corrupt"/> rather than failing the
  ///     whole listing (the containment issue #279 established for plans).</summary>
  Task<Result<ChainCatalog>> ListAsync(ChainStatus? status, CancellationToken ct = default);

  /// <summary>Compare-and-swap save: persists the chain only while the stored version still
  ///     equals <c>expectedVersion</c>; the store bumps Version on success. Fails with
  ///     ChainNotFound or VersionConflict.</summary>
  Task<Result<Chain>> SaveAsync(Chain chain, int expectedVersion, CancellationToken ct = default);

  /// <summary>The chain claiming a step for <paramref name="sessionId"/>, or null when this
  ///     session is not executing any chain step (issue #301).
  ///     <para>WHY THE STORE ANSWERS THIS: the completion fact crosses the wire carrying the
  ///     AUTHORING AGENT id, and the harness must resolve 'which chain step was this session
  ///     started for' from that id alone. Scanning every chain in the workspace and filtering
  ///     in memory would read the whole record set per fact; this is one indexed query.</para>
  ///     <para>A session whose step has been SETTLED is not assigned any more — settling
  ///     clears the claim — so a late fact from a finished session resolves to null rather
  ///     than re-advancing a chain that already moved on.</para></summary>
  Task<Result<Chain?>> FindBySessionAsync(string sessionId, CancellationToken ct = default);
}
