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
///     a routing intent rather than a different model. These are catalog reference prices,
///     not proof of the price actually charged; the response's reported cost is authoritative.</para></summary>
public interface IUsagePriceSource
{
  /// <remarks><c>promptTokens</c> is the call's PROMPT SIZE, which decides which rung of a
  ///     price ladder applies (issue #293): an endpoint that charges $3/M up to 200,000 prompt
  ///     tokens and $6/M above it bills a 300,000-token call at the upper rung, and a stamp
  ///     that ignored the size would record half the real cost. A caller that genuinely does
  ///     not know the size passes 0, which selects the base prices - the honest reading for a
  ///     call whose size is unknown, never the most expensive rung. Defaulted so an existing
  ///     caller that cannot supply a size keeps its previous (base-price) behavior.</remarks>
  /// <param name="modelId">The model id, with or without a routing-variant suffix.</param>
  /// <param name="promptTokens">The call's prompt size, or 0 when it is unknown.</param>
  /// <param name="vendorId">The vendor that serves the model, or null to ask every configured
  ///     vendor. A model id identifies a model within a vendor, so the vendor is what decides
  ///     which catalog's price table answers.</param>
  /// <param name="ct">Cancellation.</param>
  Task<UsagePrices> PricesForAsync(string modelId, int promptTokens = 0,
      string? vendorId = null, CancellationToken ct = default);
}

/// <summary>Catalog-backed <see cref="IUsagePriceSource"/>: resolves the three prices from
///     the catalog of the vendor that serves the model, memoized for the process lifetime so a
///     per-call stamp costs one dictionary read rather than a catalog walk.
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
public sealed class CatalogUsagePriceSource(IModelProviderResolver resolver) : IUsagePriceSource
{
  private readonly IModelProviderResolver _resolver =
      resolver ?? throw new ArgumentNullException(nameof(resolver));
  private readonly Dictionary<string, ModelProviderEntry> _memo = [with(StringComparer.Ordinal)];
  private readonly Lock _memoLock = new();

  public async Task<UsagePrices> PricesForAsync(string modelId, int promptTokens = 0,
      string? vendorId = null, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
    // The routing variant is a suffix on the id; the catalog key is the bare model.
    string baseModel = ModelVariants.BaseOf(modelId);
    // THE MEMO IS KEYED BY VENDOR TOO: the same id under two vendors is two price tables.
    string memoKey = $"{vendorId}\u001F{baseModel}";
    lock (_memoLock)
    {
      if (_memo.TryGetValue(memoKey, out ModelProviderEntry? memoized))
      {
        return From(memoized, promptTokens);
      }
    }

    ModelProviderEntry? entry = await CheapestAsync(baseModel, vendorId, ct).ConfigureAwait(false);
    if (entry is null)
    {
      return UsagePrices.Unknown;
    }

    lock (_memoLock)
    {
      _memo[memoKey] = entry;
    }

    return From(entry, promptTokens);
  }

  /// <summary>The cheapest priced endpoint of the model among the vendors to ask, or null when
  ///     none of them lists a priced row.</summary>
  /// <param name="baseModel">The base model id.</param>
  /// <param name="vendorId">The vendor to ask, or null for every configured vendor.</param>
  /// <param name="ct">Cancellation.</param>
  private async Task<ModelProviderEntry?> CheapestAsync(string baseModel, string? vendorId,
      CancellationToken ct)
  {
    ModelProviderEntry? cheapest = null;
    foreach (string vendor in vendorId is { Length: > 0 } named ? [named] : ModelVendors.All)
    {
      Result<IModelCatalog> catalog = _resolver.Catalog(vendor);
      if (!catalog.IsSuccess)
      {
        continue;
      }

      Result<IReadOnlyList<ModelProviderEntry>> entries =
          await catalog.Value.GetAsync(ct).ConfigureAwait(false);
      if (!entries.IsSuccess)
      {
        continue;
      }

      ModelProviderEntry? candidate = entries.Value
          .Where(e => e.ModelId == baseModel)
          .Where(e => e.PromptPricePerToken is >= 0m)
          .OrderBy(e => e.PromptPricePerToken ?? decimal.MaxValue)
          .FirstOrDefault();
      if (candidate is not null
          && (cheapest is null
              || candidate.PromptPricePerToken < cheapest.PromptPricePerToken))
      {
        cheapest = candidate;
      }
    }

    return cheapest;
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
