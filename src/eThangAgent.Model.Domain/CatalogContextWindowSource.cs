using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

/// <summary>Read-only lookup of a model's context-window size from the provider catalog that
///     serves it. Null means "no authoritative window" - no catalog lists the model, or every
///     read failed; callers take their fallback path either way.
///     <para>THE VENDOR IS PART OF THE QUESTION. A model id identifies a model within a vendor,
///     not across vendors, and each vendor's catalog is a separate read. A session holds an
///     ordered list whose entries each name their own vendor, so a lookup that named only the
///     model could not say which catalog to ask.</para>
///     <para>A NULL VENDOR MEANS "WHEREVER IT LIVES": the lookup asks every configured vendor
///     and answers with the first that lists the model. That is what resolves an entry written
///     before vendors existed, and a persisted pin - which records a model and not a vendor -
///     without inventing a vendor for either.</para>
///     <para>THE KEY IS THE BASE MODEL. The catalog carries one row per routable endpoint, all
///     sharing the model id, and a routing variant (<c>:nitro</c> / <c>:floor</c>) is a SUFFIX
///     on that id naming a routing intent rather than a different model, so it is stripped
///     before the lookup.</para></summary>
public interface IContextWindowSource
{
  /// <summary>The model's context window, or null when no catalog states one.</summary>
  /// <param name="modelId">The model id, with or without a routing-variant suffix.</param>
  /// <param name="vendorId">The vendor that serves it, or null to ask every configured
  ///     vendor.</param>
  /// <param name="ct">Cancellation.</param>
  Task<int?> WindowForAsync(string modelId, string? vendorId = null, CancellationToken ct = default);
}

/// <summary>Catalog-backed <see cref="IContextWindowSource"/>: resolves context windows
///     through the vendor-keyed catalogs this container wired.
///     <para>THE WIDEST WINDOW OF THE MODEL'S ENDPOINTS WINS. The endpoints of one model can
///     report different windows, and the budget must fit the endpoint that actually serves -
///     which is not knowable before the request. Taking the widest is the honest reading for a
///     router: it is the window the user is entitled to when routing picks the best endpoint,
///     and it never under-budgets a turn whose context would have fit.</para>
///     <para>A VENDOR WITH NO CATALOG IS SKIPPED, NOT FATAL: a credential that was never
///     configured leaves that vendor absent, and a model the other vendors list still
///     resolves.</para></summary>
/// <param name="resolver">The vendor-keyed catalog resolution.</param>
public sealed class CatalogContextWindowSource(IModelProviderResolver resolver) : IContextWindowSource
{
  private readonly IModelProviderResolver _resolver =
      resolver ?? throw new ArgumentNullException(nameof(resolver));

  /// <inheritdoc />
  public async Task<int?> WindowForAsync(string modelId, string? vendorId = null,
      CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
    string baseModel = ModelVariants.BaseOf(modelId);
    foreach (string vendor in VendorsFor(vendorId))
    {
      if (await WindowFromAsync(vendor, baseModel, ct).ConfigureAwait(false) is { } window)
      {
        return window;
      }
    }

    return null;
  }

  /// <summary>The vendors to ask: the one named, or every vendor this build knows when none
  ///     was named. A blank id is no id.</summary>
  /// <param name="vendorId">The vendor the caller named, or null.</param>
  private static IReadOnlyList<string> VendorsFor(string? vendorId) =>
      string.IsNullOrWhiteSpace(vendorId) ? ModelVendors.All : [vendorId];

  /// <summary>One vendor's answer for the model, or null when that vendor has no catalog, the
  ///     read failed, or it does not list the model.</summary>
  /// <param name="vendor">The vendor id.</param>
  /// <param name="baseModel">The base model id.</param>
  /// <param name="ct">Cancellation.</param>
  private async Task<int?> WindowFromAsync(string vendor, string baseModel, CancellationToken ct)
  {
    Result<IModelCatalog> catalog = _resolver.Catalog(vendor);
    if (!catalog.IsSuccess)
    {
      return null;
    }

    Result<IReadOnlyList<ModelProviderEntry>> entries =
        await catalog.Value.GetAsync(ct).ConfigureAwait(false);
    return entries.IsSuccess
        ? entries.Value.Where(e => e.ModelId == baseModel).Max(e => (int?)e.ContextLength)
        : null;
  }
}
