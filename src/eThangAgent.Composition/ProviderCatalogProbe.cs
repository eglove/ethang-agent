using eThangAgent.Cloudflare.ACL;
using eThangAgent.ModelDomain;
using eThangAgent.OpenRouter.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;

namespace eThangAgent.Composition;

/// <summary>The outcome of pulling one vendor's catalog with a credential that is not saved yet:
///     how many models the credential unlocks, or the named reason it unlocks none.
///     <para>A COUNT AND A FAILURE ARE DIFFERENT FACTS, so they are different cases rather than
///     a zero that could mean either.</para></summary>
/// <param name="Vendor">The vendor id the probe read.</param>
/// <param name="Entries">The models the credential unlocks, as the vendor lists them.</param>
public sealed record ProviderProbeResult(string Vendor, IReadOnlyList<ModelProviderEntry> Entries)
{
  /// <summary>The vendor's name as a surface renders it.</summary>
  public string DisplayName => ModelVendors.DisplayName(Vendor);

  /// <summary>How many models the credential unlocks.</summary>
  public int ModelCount => Entries.Count;

  /// <summary>The outcome in one line: what the credential unlocks, or that it unlocks
  ///     nothing.</summary>
  public string Summary => ModelCount switch
  {
    0 => $"{DisplayName} answered, but lists no chat models for this credential.",
    1 => $"{DisplayName} works: 1 model is available.",
    _ => $"{DisplayName} works: {ModelCount} models are available.",
  };
}

/// <summary>Pulls a provider's model catalog from credential values that are NOT saved yet, so a
///     settings surface can say whether a key works and what it unlocks before the dialog is
///     confirmed.
///     <para>THE PULL IS A REFRESH, NOT A READ: the catalog a session already holds is replaced
///     with what the source answers now, and the replacement is persisted through the same
///     <see cref="IModelCatalogCache"/> the session's own catalog hydrates from. That is what
///     makes the models appear in the model search without restarting the app - the credential
///     is live on the session's carrier, and the catalog the search reads has already been
///     refilled.</para>
///     <para>THE PROBE BUILDS ITS OWN CATALOG over the credential it was handed, rather than
///     reusing a session's: the values being tested are the form's, which may differ from
///     anything a container was built with, and a probe that read the session's credential would
///     answer a question nobody asked.</para></summary>
/// <param name="database">The app database the pulled catalog is persisted into, so the
///     session's own catalog serves it on the next read.</param>
/// <param name="httpFactory">Builds the client a pull goes through, or null for a fresh one.
///     A pull is a handful of calls, so the client's lifetime is the pull's.</param>
public sealed class ProviderCatalogProbe(AppDatabase database, Func<HttpClient>? httpFactory = null)
{
  private readonly AppDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

  private readonly Func<HttpClient> _httpFactory = httpFactory ?? DefaultClient;

  /// <summary>Pulls one vendor's catalog with the supplied credential.
  ///     <para>AN UNKNOWN VENDOR IS A NAMED FAILURE, never a silent success: a surface that
  ///     offered a probe for a vendor this build cannot read would report a working credential
  ///     for a provider that does not exist.</para></summary>
  /// <param name="vendor">The vendor id to read.</param>
  /// <param name="accountId">The Cloudflare account id, or null for a vendor that needs none.
  /// </param>
  /// <param name="apiToken">The Cloudflare API token, or null for a vendor that needs none.
  /// </param>
  /// <param name="openRouterBaseUrl">The OpenRouter host to read, or null for the provider
  ///     default.</param>
  /// <param name="ct">Cancellation.</param>
  public async Task<Result<ProviderProbeResult>> ProbeAsync(string vendor, string? accountId,
      string? apiToken, Uri? openRouterBaseUrl, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(vendor);
    IModelCatalog catalog;
    switch (vendor)
    {
      case ModelVendors.OpenRouter:
        catalog = OpenRouterCatalog(openRouterBaseUrl);
        break;
      case ModelVendors.Cloudflare:
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(apiToken))
        {
          return Result.Failure<ProviderProbeResult>(new DomainError("ProviderNotConfigured",
              "Cloudflare needs both an account id and an API token before its models can be read."));
        }

        catalog = CloudflareCatalog(accountId, apiToken);
        break;
      default:
        return Result.Failure<ProviderProbeResult>(new DomainError("UnknownProvider",
            $"Unknown provider '{vendor}'. Known providers: {string.Join(", ", ModelVendors.All)}."));
    }

    // The catalog owns the client it was built over, so the pull disposes both.
    using IDisposable owned = (IDisposable)catalog;
    Result<IReadOnlyList<ModelProviderEntry>> pulled = await catalog.RefreshAsync(ct)
        .ConfigureAwait(false);
    return pulled.IsSuccess
        ? Result.Success(new ProviderProbeResult(vendor, pulled.Value))
        : Result.Failure<ProviderProbeResult>(pulled.Error);
  }

  /// <summary>An OpenRouter catalog over the supplied host, sharing the app's persisted snapshot
  ///     store so a successful pull is what the session's catalog serves next.
  ///     <para>THE CLIENT'S LIFETIME IS THE PROBE'S: a pull is a handful of calls, and the
  ///     catalog that owns the client is disposed when the pull ends.</para></summary>
  /// <param name="baseUrl">The host to read, or null for the provider default.</param>
  private OpenRouterCatalogClient OpenRouterCatalog(Uri? baseUrl)
  {
    Uri host = baseUrl ?? new Uri(OpenRouterSettings.DefaultBaseUrl);
    HttpClient http = _httpFactory();
    OpenRouterAppAttribution.Apply(http);
    return new OpenRouterCatalogClient(http, new OpenRouterConfiguration(string.Empty, host),
        new SqliteModelCatalogCache(_database), Providers.OpenRouter);
  }

  /// <summary>A Cloudflare catalog over the supplied credential, sharing the app's persisted
  ///     snapshot store under the same account-scoped key the session's catalog uses.</summary>
  /// <param name="accountId">The account id to read.</param>
  /// <param name="apiToken">The API token to present.</param>
  private CloudflareModelCatalog CloudflareCatalog(string accountId, string apiToken)
  {
    CloudflareConfiguration configuration = new(accountId, apiToken,
        new Uri(CloudflareSettings.ApiBaseUrl));
    return new CloudflareModelCatalog(_httpFactory(), configuration,
        new SqliteModelCatalogCache(_database));
  }

  /// <summary>The client a pull goes through when the host supplied none. The catalog the pull
  ///     builds owns it and disposes it when the pull ends.</summary>
  private static HttpClient DefaultClient() => new() { Timeout = TimeSpan.FromSeconds(120) };
}
