using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

/// <summary>Provides a provider's model catalog entries.</summary>
public interface IModelCatalog
{
  Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default);

  /// <summary>Revalidates against the source, joining an in-flight refresh. A caller's
  /// cancellation cancels only its wait; cached data survives a refresh failure.</summary>
  Task<Result<IReadOnlyList<ModelProviderEntry>>> RefreshAsync(CancellationToken ct = default)
      => GetAsync(ct);

  /// <summary>The cache-hit rates providers publish for their endpoints, keyed by
  ///     <see cref="ModelProviderEntry.Key"/>, or empty when none are available (issue #296
  ///     follow-up).
  ///     <para>WHY IT IS A SEPARATE READ: the rates are an ENRICHMENT of the catalog, not part
  ///     of it. A catalog that loaded but whose statistics did not is still a usable catalog,
  ///     so this never fails and never blocks the entries - an empty result simply lets the
  ///     resolution chain fall through to the documented default.</para></summary>
  Task<IReadOnlyDictionary<string, PublishedProviderStats>> GetPublishedStatsAsync(
      CancellationToken ct = default)
      => Task.FromResult<IReadOnlyDictionary<string, PublishedProviderStats>>(
          new Dictionary<string, PublishedProviderStats>(StringComparer.Ordinal));

  /// <summary>The endpoints the provider states are eligible under a retention tier, or NULL
  ///     when that was never established.
  ///     <para>NULL AND EMPTY ARE DIFFERENT FACTS, and a caller enforcing a tier must not confuse
  ///     them. Null means the question was not answered - the source was never consulted or the
  ///     read failed - so nothing is known and a turn is sent with the restriction on for the
  ///     provider to judge. Empty means the source answered and named nothing, which is proof
  ///     that no eligible endpoint serves any model.</para>
  ///     <para>ONE READ SERVES EVERY TIER: the list carries each endpoint's own retention fact,
  ///     so a caller asks which of them its tier admits rather than fetching a second list per
  ///     tier.</para></summary>
  Task<RetentionEligibleList?> GetRetentionEligibleAsync(CancellationToken ct = default)
      => Task.FromResult<RetentionEligibleList?>(null);
}
