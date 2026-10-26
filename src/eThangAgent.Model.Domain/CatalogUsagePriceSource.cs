using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

/// <summary>The three per-token prices a usage row stamps at write time (issue #22), all
/// nullable: a null price means the price is unknown, so the row contributes tokens but no
/// cost — cost reads as null, never fabricated.</summary>
public sealed record UsagePrices(decimal? PromptPricePerToken, decimal? PromptCacheReadPricePerToken,
    decimal? CompletionPricePerToken)
{
  /// <summary>The price set for a call whose prices are entirely unknown.</summary>
  public static UsagePrices Unknown { get; } = new(null, null, null);
}

/// <summary>Resolves the prices to stamp on a usage row for one model (issue #22). A
/// missing model, a model with no advertised prices, or a failed catalog read all yield
/// <see cref="UsagePrices.Unknown"/> — cost is never fabricated from a guess.</summary>
public interface IUsagePriceSource
{
  Task<UsagePrices> PricesForAsync(string modelId, string? providerName, CancellationToken ct = default);
}

/// <summary>Catalog-backed <see cref="IUsagePriceSource"/>: resolves the three prices from
/// the provider catalog supplied by <see cref="IModelCatalog"/>, memoized for the process
/// lifetime so a per-call stamp costs one dictionary read rather than a catalog walk.
///     <para>Memoization is safe because the catalog's price for a (model, provider) pair
///     is itself a cached current row — the catalog does not carry price history, so a
///     re-resolve within one process cannot produce a different answer for a call that
///     already happened. A FAILED catalog read is NOT memoized: a transient failure must
///     not poison every later stamp with nulls for the process lifetime.</para>
///     <para>Selection matches <see cref="CatalogContextWindowSource"/>: the first entry
///     with the same model id whose provider name is null or equal. A model whose entry
///     advertises no usable prices (the routing pseudo-models' -1, "set at routing time")
///     resolves to <see cref="UsagePrices.Unknown"/>.</para></summary>
public sealed class CatalogUsagePriceSource(IModelCatalog catalog) : IUsagePriceSource
{
  private readonly IModelCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
  private readonly Dictionary<(string ModelId, string? Provider), UsagePrices> _memo = [];
  private readonly Lock _memoLock = new();

  public async Task<UsagePrices> PricesForAsync(string modelId, string? providerName, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
    (string ModelId, string? Provider) key = (modelId, providerName);
    lock (_memoLock)
    {
      if (_memo.TryGetValue(key, out UsagePrices? memoized))
      {
        return memoized;
      }
    }

    Result<IReadOnlyList<ModelProviderEntry>> entries = await _catalog.GetAsync(ct).ConfigureAwait(false);
    if (!entries.IsSuccess)
    {
      return UsagePrices.Unknown;
    }

    ModelProviderEntry? entry = entries.Value.FirstOrDefault(e => e.ModelId == modelId
        && (providerName is null || e.ProviderName == providerName));
    UsagePrices prices = entry is null ? UsagePrices.Unknown : From(entry);
    if (entry is not null)
    {
      lock (_memoLock)
      {
        _memo[key] = prices;
      }
    }

    return prices;
  }

  /// <summary>The entry's advertised prices, with a negative value treated as "not a price"
  ///     (the routing pseudo-models serve -1 to mean the price is set at routing time) and
  ///     an ABSENT one staying unknown (issue #289 made the catalog prices nullable; an
  ///     unpriced model must not stamp a cost of zero). A served zero stays a real price.</summary>
  private static UsagePrices From(ModelProviderEntry entry)
      => new(PriceOrNull(entry.PromptPricePerToken),
          entry.PromptCacheReadPricePerToken is { } cacheRead ? PriceOrNull(cacheRead) : null,
          PriceOrNull(entry.CompletionPricePerToken));

  private static decimal? PriceOrNull(decimal? price) => price is { } value && value >= 0m ? value : null;
}
