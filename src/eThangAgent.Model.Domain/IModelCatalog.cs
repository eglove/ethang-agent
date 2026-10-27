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
}
