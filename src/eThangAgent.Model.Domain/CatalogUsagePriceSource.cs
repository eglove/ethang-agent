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
  /// <remarks><c>promptTokens</c> is the call's PROMPT SIZE, which decides which rung of a
  ///     price ladder applies (issue #293): an endpoint that charges $3/M up to 200,000 prompt
  ///     tokens and $6/M above it bills a 300,000-token call at the upper rung, and a stamp
  ///     that ignored the size would record half the real cost. A caller that genuinely does
  ///     not know the size passes 0, which selects the base prices - the honest reading for a
  ///     call whose size is unknown, never the most expensive rung. Defaulted so an existing
  ///     caller that cannot supply a size keeps its previous (base-price) behavior.</remarks>
  Task<UsagePrices> PricesForAsync(string modelId, string? providerName, int promptTokens = 0,
      CancellationToken ct = default);
}

/// <summary>Catalog-backed <see cref="IUsagePriceSource"/>: resolves the three prices from
/// the provider catalog supplied by <see cref="IModelCatalog"/>, memoized for the process
/// lifetime so a per-call stamp costs one dictionary read rather than a catalog walk.
///     <para>Memoization is safe because the catalog's price for a (model, provider) pair
///     is itself a cached current row — the catalog does not carry price history, so a
///     re-resolve within one process cannot produce a different answer for a call that
///     already happened. A FAILED catalog read is NOT memoized: a transient failure must
///     not poison every later stamp with nulls for the process lifetime.</para>
///     <para>WHAT IS MEMOIZED IS THE ENTRY, NOT THE PRICE (issue #293): the answer now
///     depends on the call's prompt size, so caching a resolved <see cref="UsagePrices"/>
///     per (model, provider) would serve the first call's tier to every later call - a
///     long-context call billed at the short-context rate, silently. The catalog walk is
///     what memoization exists to avoid, and that is what is cached.</para>
///     <para>Selection matches <see cref="CatalogContextWindowSource"/>: the first entry
///     with the same model id whose provider name is null or equal. A model whose entry
///     advertises no usable prices (the routing pseudo-models' -1, "set at routing time")
///     resolves to <see cref="UsagePrices.Unknown"/>.</para></summary>
public sealed class CatalogUsagePriceSource(IModelCatalog catalog) : IUsagePriceSource
{
  private readonly IModelCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
  private readonly Dictionary<(string ModelId, string? Provider), ModelProviderEntry> _memo = [];
  private readonly Lock _memoLock = new();

  public async Task<UsagePrices> PricesForAsync(string modelId, string? providerName, int promptTokens = 0,
      CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
    (string ModelId, string? Provider) key = (modelId, providerName);
    lock (_memoLock)
    {
      if (_memo.TryGetValue(key, out ModelProviderEntry? memoized))
      {
        return From(memoized, promptTokens);
      }
    }

    Result<IReadOnlyList<ModelProviderEntry>> entries = await _catalog.GetAsync(ct).ConfigureAwait(false);
    if (!entries.IsSuccess)
    {
      return UsagePrices.Unknown;
    }

    // MATCHING IS BY THE ROUTING SLUG, not the provider name (issue #296 follow-up): the
    // caller passes the slug the pin carries, and several entries share one provider name
    // (five grok-4.7 endpoints are all "xAI"), so a name match would stamp one endpoint's
    // prices on a call another endpoint served.
    //
    // A NAME IS NOT AN ENDPOINT, and taking the first name match is how this went wrong
    // (measured 2026-10-27: a bare "DeepInfra" pin stamped the flex price on 93 rows spanning
    // flex, fp8 and priority). The slug rung is therefore tried FIRST and on its own; the name
    // rung answers only when no entry carries that slug, and then only when exactly ONE entry
    // shares the name - because with several, no answer is the honest one. Stamping a price
    // nobody can attribute is the same class of lie as stamping the wrong one, and the ledger
    // reports an unknown price as unpriced rather than priced-at-a-guess.
    ModelProviderEntry? entry = Resolve(entries.Value, modelId, providerName);
    if (entry is null)
    {
      return UsagePrices.Unknown;
    }

    lock (_memoLock)
    {
      _memo[key] = entry;
    }

    return From(entry, promptTokens);
  }

  /// <summary>The catalog entry a call's prices come from, or null when no entry can be
  ///     attributed to it. Slug first (unambiguous - one endpoint), then a name that identifies
  ///     exactly one entry, and otherwise NOTHING: an ambiguous name must leave the call
  ///     unpriced rather than priced from whichever variant happened to sort first.</summary>
  private static ModelProviderEntry? Resolve(IReadOnlyList<ModelProviderEntry> entries,
      string modelId, string? providerName)
  {
    if (providerName is null)
    {
      return entries.FirstOrDefault(e => e.ModelId == modelId);
    }

    if (entries.FirstOrDefault(e => e.ModelId == modelId
        && string.Equals(e.RoutingSlug, providerName, StringComparison.Ordinal)) is { } bySlug)
    {
      return bySlug;
    }

    List<ModelProviderEntry> byName =
    [
        .. entries.Where(e => e.ModelId == modelId
            && string.Equals(e.ProviderName, providerName, StringComparison.Ordinal)),
    ];
    return byName.Count == 1 ? byName[0] : null;
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
