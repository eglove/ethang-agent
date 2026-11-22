namespace eThangAgent.ModelDomain;

/// <summary>The VENDOR vocabulary: which provider serves a model. One id per vendor, and the
///     human-facing name each surface renders.
///     <para>A VENDOR IS NOT A ROUTING PIN. <see cref="ModelProviderChoice.ProviderName"/> names an
///     OpenRouter ENDPOINT to pin (<c>provider.only</c>), which is a different question from which
///     company serves the model. Two vendors may serve the same model id, and the harness treats
///     those as two rows because they differ in price and in quota.</para>
///     <para>THIS TYPE LIVES IN THE MODEL DOMAIN, and the composition root's <c>Providers</c>
///     delegates to it. The dependency runs Composition -> Model.Domain, so a Model.Domain type
///     cannot call <c>Providers</c>; the vocabulary therefore sits on the side both can reach, and
///     there is exactly one copy of each literal.</para>
///     <para><see cref="DisplayName"/> IS TOTAL: an id this build does not know renders as its raw
///     id instead of throwing, because it reads historical records and a list surface must never
///     crash on a row it cannot name.</para></summary>
public static class ModelVendors
{
  /// <summary>OpenRouter, served through its own ACL.</summary>
  public const string OpenRouter = "openrouter";

  /// <summary>Cloudflare Workers AI, served through its own ACL.</summary>
  public const string Cloudflare = "cloudflare";

  /// <summary>Every vendor id this build knows, in the order a surface offers them.</summary>
  public static IReadOnlyList<string> All { get; } = [OpenRouter, Cloudflare];

  /// <summary>True when <paramref name="vendorId"/> names a vendor this build knows. Null and a
  ///     blank string are NOT known: an unnamed vendor is a different fact from a named one.
  ///     </summary>
  public static bool IsKnown(string? vendorId) => vendorId is OpenRouter or Cloudflare;

  /// <summary>The human-facing name of a vendor, or the raw id when this build does not know it.
  ///     </summary>
  public static string DisplayName(string vendorId) => vendorId switch
  {
    OpenRouter => "OpenRouter",
    Cloudflare => "Cloudflare",
    _ => vendorId,
  };
}
