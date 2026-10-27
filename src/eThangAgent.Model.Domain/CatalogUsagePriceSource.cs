using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

/// <summary>The three per-token prices a usage row stamps at write time (issue #22), all
///     nullable: a null price means the price is unknown, so the row contributes tokens but no
///     cost — cost reads as null, never fabricated.</summary>
public sealed record UsagePrices(decimal? PromptPricePerToken, decimal? PromptCacheReadPricePerToken,
    decimal? CompletionPricePerToken)
{
  /// <summary>The price set for a call whose prices are entirely unknown.</summary>
  public static UsagePrices Unknown { get; } = new(null, null, null);
}

/// <summary>Resolves the prices to stamp on a usage row for one model (issue #22). A missing
///     model, a model with no advertised prices, or a failed catalog read all yield
///     <see cref="UsagePrices.Unknown"/> — cost is never fabricated from a guess.
///     <para>THE KEY IS THE BASE MODEL, and there is deliberately no provider parameter: the
///     catalog serves one row per ROUTABLE ENDPOINT of a model, all sharing the model id, so a
///     model id alone identifies the price table. The routing variant (<c>:nitro</c> /
///     <c>:floor</c>) is a SUFFIX on that id and is stripped before this call, because it names
///     a routing intent rather than a different model — the prices that apply are the served
///     endpoint's, which the response itself reports.</para></summary>
public interface IUsagePriceSource
{
  /// <remarks><c>promptTokens</c> is the call's PROMPT SIZE, which decides which rung of a
  ///     price ladder applies (issue #293): an endpoint that charges $3/M up to 200,000 prompt
  ///     tokens and $6/M above it bills a 300,000-token call at the upper rung, and a stamp
  ///     that ignored the size would record half the real cost. A caller that genuinely does
  ///     not know the size passes 0, which selects the base prices - the honest reading for a
  ///     call whose size is unknown, never the most expensive rung. Defaulted so an existing
  ///     caller that cannot supply a size keeps its previous (base-price) behavior.</remarks>
  Task<UsagePrices> PricesForAsync(string modelId, int promptTokens = 0,
      CancellationToken ct = default);
}

/// <summary>Catalog-backed <see cref="IUsagePriceSource"/>: resolves the three prices from
///     the provider catalog supplied by <see cref="IModelCatalog"/>, memoized for the process
///     lifetime so a per-call stamp costs one dictionary read rather than a catalog walk.
///     <para>Memoization is safe because the catalog's price for a model is itself a cached
///     current row — the catalog does not carry price history, so a re-resolve within one
///     process cannot produce a different answer for a call that already happened. A FAILED
///     catalog read is NOT memoized: a transient failure must not poison every later stamp
///     with nulls for the process lifetime.</para>
///     <para>WHAT IS MEMOIZED IS THE ENTRY, NOT THE PRICE (issue #293): the answer depends on
///     the call's prompt size, so caching a resolved <see cref="UsagePrices"/> per model would
///     serve the first call's tier to every later call - a long-context call billed at the
///     short-context rate, silently. The catalog walk is what memoization exists to avoid, and
///     that is what is cached.</para>
///     <para>WHICH ENDPOINT OF A MODEL ANSWERS: the CHEAPEST one that advertises prices,
///     because that is the row the picker summarises the model by and the row a request with no
///     pin can actually be served from. Measured live, a model's endpoints differ in price, so
///     picking arbitrarily (the first row the API happened to list) would stamp a different
///     endpoint's prices on every call and make the ledger's effective pricing meaningless. A
///     model whose rows advertise no usable prices (the routing pseudo-models' -1, "set at
///     routing time") resolves to <see cref="UsagePrices.Unknown"/>.</para></summary>
public sealed class CatalogUsagePriceSource(IModelCatalog catalog) : IUsagePriceSource
{
  private readonly IModelCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
  private readonly Dictionary<string, ModelProviderEntry> _memo = new(StringComparer.Ordinal);
  private readonly Lock _memoLock = new();

  public async Task<UsagePrices> PricesForAsync(string modelId, int promptTokens = 0,
      CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
    // The routing variant is a suffix on the id; the catalog key is the bare model.
    string baseModel = ModelVariants.BaseOf(modelId);
    lock (_memoLock)
    {
      if (_memo.TryGetValue(baseModel, out ModelProviderEntry? memoized))
      {
        return From(memoized, promptTokens);
      }
    }

    Result<IReadOnlyList<ModelProviderEntry>> entries = await _catalog.GetAsync(ct).ConfigureAwait(false);
    if (!entries.IsSuccess)
    {
      return UsagePrices.Unknown;
    }

    ModelProviderEntry? entry = entries.Value
        .Where(e => e.ModelId == baseModel)
        .Where(e => e.PromptPricePerToken is >= 0m)
        .OrderBy(e => e.PromptPricePerToken ?? decimal.MaxValue)
        .FirstOrDefault();
    if (entry is null)
    {
      return UsagePrices.Unknown;
    }

    lock (_memoLock)
    {
      _memo[baseModel] = entry;
    }

    return From(entry, promptTokens);
  }

  /// <summary>The entry's prices for a call of this prompt size (issue #293): the applicable
  ///     ladder rung when the endpoint serves one, else the base prices. A negative value is
  ///     treated as "not a price" (the routing pseudo-models serve -1 to mean the price is set
  ///     at routing time) and an ABSENT one stays unknown (issue #289 made the catalog prices
  ///     nullable; an unpriced model must not stamp a cost of zero). A served zero stays a real
  ///     price.</summary>
  private static UsagePrices From(ModelProviderEntry entry, int promptTokens)
  {
    UsagePrices tier = entry.PricesFor(promptTokens);
    return new(PriceOrNull(tier.PromptPricePerToken),
        tier.PromptCacheReadPricePerToken is { } cacheRead ? PriceOrNull(cacheRead) : null,
        PriceOrNull(tier.CompletionPricePerToken));
  }

  private static decimal? PriceOrNull(decimal? price) => price is { } value && value >= 0m ? value : null;
}
