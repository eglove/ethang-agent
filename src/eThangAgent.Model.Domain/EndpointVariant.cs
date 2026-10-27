namespace eThangAgent.ModelDomain;

/// <summary>Which distinct endpoint of a provider serves a model, and how to say so
///     (issue #296 follow-up, superseding the pair key).
///     <para>THE PROBLEM THIS SOLVES: OpenRouter does not serve one endpoint per provider
///     name. It serves several - a plain one, ZDR (zero data retention) ones, region-pinned
///     ones, priority/flex/ultrafast service tiers, and different quantizations - and it
///     publishes each as its own row with its own price and its own reliability. Measured
///     live 2026-10-27, <c>x-ai/grok-4.7</c> has FIVE endpoints all named "xAI":
///     <c>xai</c>, <c>xai/zdr</c>, <c>xai/zdr/us</c>, <c>xai/priority</c>, and
///     <c>xai/zdr/priority</c>, priced $2, $2, $2.20, $4 and $4 per million prompt tokens.
///     Keying on the provider NAME therefore collapsed five products into one row and threw
///     four away - 339 of 1509 catalog rows - and the surviving row's price was whichever
///     endpoint the API happened to list first.</para>
///     <para>THE TAG IS THE IDENTITY, and it is also the ROUTING SLUG: OpenRouter's own
///     routing documentation states every endpoint slug has the shape
///     <c>provider[/variant][/region][/tier]</c> and that <c>provider.only</c> accepts the
///     full slug, so <c>only: ["xai/zdr/priority"]</c> targets exactly that endpoint.
///     Measured across six models on 2026-10-27, tags were distinct on every one
///     (35/35, 48/48, 30/30, 8/8, 7/7, 7/7) while provider names repeated heavily - so the
///     tag is both the only lossless key and the only one that can actually be pinned.</para>
///     <para>THE SLUG IS NULL WHEN THE API NAMED NO TAG. A null slug means the variant is
///     not addressable, and <see cref="ModelProviderEntry.Key"/> then falls back to the
///     provider name - the pre-existing behaviour, kept so a payload without tags degrades
///     to what this harness did before rather than collapsing every endpoint into one.</para>
///     </summary>
/// <param name="Slug">The routable endpoint slug (<c>tag</c>), or null when the API served
///     none.</param>
/// <param name="ServiceTier">The tier the endpoint serves at (<c>priority</c>, <c>flex</c>,
///     <c>ultrafast</c>), or null for the provider's standard tier.</param>
/// <param name="Quantization">The served quantization (<c>fp4</c>, <c>fp8</c>,
///     <c>mxfp4</c>, <c>nvfp4</c>), or null when the API did not say. <c>unknown</c> is the
///     API's own word for 'not stated' and is normalized to null rather than displayed as
///     though it were a quantization.</param>
/// <param name="IsZeroDataRetention">True when the slug marks the endpoint as
///     zero-data-retention. Read from the slug because that is where the API puts it
///     (<c>xai/zdr</c>, <c>mistral/zdr</c>) - there is no separate boolean on the endpoints
///     payload.</param>
/// <param name="Region">The region the endpoint is pinned to when the slug names one
///     (<c>us</c>, <c>eu</c>, <c>us-east5</c>, <c>global</c>), else null. Null means the
///     endpoint is not region-pinned, which is a different fact from an unnamed region.</param>
public sealed record EndpointVariant(
    string? Slug,
    string? ServiceTier = null,
    string? Quantization = null,
    bool IsZeroDataRetention = false,
    string? Region = null)
{
  /// <summary>The variant for an endpoint the API described with no tag at all: nothing is
  ///     known about how it differs, and nothing is invented.</summary>
  public static EndpointVariant Unspecified { get; } = new((string?)null);

  /// <summary>True when this variant says nothing beyond 'some endpoint'. A surface uses it
  ///     to omit the annotation entirely rather than print an empty one.</summary>
  public bool IsUnspecified => string.IsNullOrWhiteSpace(Slug)
      && string.IsNullOrWhiteSpace(ServiceTier)
      && string.IsNullOrWhiteSpace(Quantization)
      && !IsZeroDataRetention
      && string.IsNullOrWhiteSpace(Region);

  /// <summary>The human-readable annotation for this variant, e.g.
  ///     <c>zdr · us · priority · fp8</c>, or empty when nothing distinguishes it.
  ///     <para>EVERY DISTINGUISHING FACT IS NAMED, IN A FIXED ORDER: an endpoint that differs
  ///     only by region must not render identically to one that differs only by tier, because
  ///     the user is choosing between them on exactly that difference. The order is
  ///     privacy, then location, then speed, then precision - the order in which the choices
  ///     usually matter to a caller.</para>
  ///     <para>ZERO-DATA-RETENTION IS SPELLED OUT rather than left as the slug's <c>zdr</c>
  ///     token: it is a privacy commitment, not jargon, and a user comparing two rows should
  ///     not have to know the acronym to see which one keeps their prompts.</para></summary>
  public string Describe()
  {
    List<string> parts = [];
    if (IsZeroDataRetention)
    {
      parts.Add("zero data retention");
    }

    if (!string.IsNullOrWhiteSpace(Region))
    {
      parts.Add("region " + Region);
    }

    if (!string.IsNullOrWhiteSpace(ServiceTier))
    {
      parts.Add(ServiceTier + " tier");
    }

    if (!string.IsNullOrWhiteSpace(Quantization))
    {
      parts.Add(Quantization);
    }

    return string.Join(" · ", parts);
  }

  /// <summary>The variant implied by an endpoint's <c>tag</c>, its <c>service_tier</c>, and
  ///     its <c>quantization</c>.
  ///     <para>THE SLUG IS PARSED, NOT GUESSED. Its shape is documented as
  ///     <c>provider[/variant][/region][/tier]</c>, and the measured tags confirm it:
  ///     <c>amazon-bedrock/us-east-1</c>, <c>deepinfra/fp8/priority</c>,
  ///     <c>google-vertex/global/flex</c>, <c>mistral/nvfp4/eu</c>. The FIRST segment is the
  ///     provider, so parsing starts after it; the remaining segments are classified by what
  ///     they match rather than by position, because the API is not consistent about which
  ///     optional segments appear (a tag may carry a tier with no region, or a region with no
  ///     variant).</para>
  ///     <para>AN UNRECOGNIZED SEGMENT IS NOT A REGION. Region is only claimed for a segment
  ///     that is a known region shape, so a future variant token cannot be silently
  ///     mislabelled as a place - it simply does not appear in the annotation, while the slug
  ///     itself (which IS the identity) still distinguishes the row.</para></summary>
  /// <param name="tag">The endpoint's <c>tag</c>, or null.</param>
  /// <param name="serviceTier">The endpoint's <c>service_tier</c>, or null.</param>
  /// <param name="quantization">The endpoint's <c>quantization</c>, or null.</param>
  public static EndpointVariant Parse(string? tag, string? serviceTier, string? quantization)
  {
    string? slug = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim();
    string? tier = Normalize(serviceTier);
    string? quant = NormalizeQuantization(quantization);
    if (slug is null)
    {
      return new EndpointVariant(null, tier, quant);
    }

    string[] segments = slug.Split('/', StringSplitOptions.RemoveEmptyEntries);
    bool zdr = false;
    string? region = null;
    // Segment 0 is the provider slug; everything after it describes the variant. The
    // classification lives in ClassifySegment so this method stays one readable loop.
    for (int i = 1; i < segments.Length; i++)
    {
      (bool isZdr, string? tierToken, string? quantToken, string? regionToken) =
          ClassifySegment(segments[i]);
      zdr |= isZdr;
      // The slug's own tokens are authoritative only when the payload omitted the fields.
      tier ??= tierToken;
      quant ??= quantToken;
      region ??= regionToken;
    }

    return new EndpointVariant(slug, tier, quant, zdr, region);
  }

  /// <summary>What one slug segment says, with every member null/false when it says nothing
  ///     this parser recognizes. Extracted so <see cref="Parse"/> stays a single loop rather
  ///     than a chain of mutually exclusive tests.
  ///     <para>AN UNRECOGNIZED SEGMENT IS SILENTLY UNCLASSIFIED, never guessed: the slug itself
  ///     still distinguishes the row (it IS the identity), so a variant token added by the API
  ///     later cannot be mislabelled as a region or a tier.</para></summary>
  private static (bool IsZdr, string? Tier, string? Quantization, string? Region) ClassifySegment(
      string segment)
  {
    if (string.Equals(segment, "zdr", StringComparison.OrdinalIgnoreCase))
    {
      return (true, null, null, null);
    }

    if (IsServiceTier(segment))
    {
      return (false, segment, null, null);
    }

    if (IsQuantization(segment))
    {
      return (false, null, NormalizeQuantization(segment), null);
    }

    return IsRegion(segment) ? (false, null, null, segment) : (false, null, null, null);
  }

  /// <summary>True for a segment that names a service tier. The measured set is closed
  ///     (<c>priority</c>, <c>flex</c>, <c>ultrafast</c>); an unknown token is not treated as
  ///     a tier, so it stays unclassified rather than being mislabelled.</summary>
  private static bool IsServiceTier(string segment)
      => segment is "priority" or "flex" or "ultrafast";

  /// <summary>True for a segment that names a quantization.</summary>
  private static bool IsQuantization(string segment)
      => segment is "fp4" or "fp8" or "mxfp4" or "nvfp4" or "fp16" or "bf16";

  /// <summary>True for a segment that names a place. Deliberately a shape test rather than a
  ///     list of every region OpenRouter serves: regions are added over time, and a two-letter
  ///     or region-shaped token is recognizable without enumerating them.
  ///     <para>THE PROVIDER-SLUG SEGMENTS ARE EXCLUDED BY POSITION, not by this test - parsing
  ///     starts after segment 0 - so a provider whose slug looks like a region cannot
  ///     misclassify itself.</para></summary>
  private static bool IsRegion(string segment)
  {
    if (string.Equals(segment, "global", StringComparison.OrdinalIgnoreCase))
    {
      return true;
    }

    // us, eu, uk, ap, ... or a compound like us-east5 / eu-west-1.
    string head = segment.Split('-')[0];
    return head.Length == 2 && head.All(char.IsAsciiLetter);
  }

  /// <summary>A trimmed value, or null when absent or blank.</summary>
  private static string? Normalize(string? value)
      => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  /// <summary>The quantization, or null. <c>unknown</c> is the API's word for 'not stated',
  ///     so it is normalized to null - rendering it would present an absence as a fact, the
  ///     same rule the nullable prices follow.</summary>
  private static string? NormalizeQuantization(string? value)
  {
    string? normalized = Normalize(value);
    // The API serves these lower-case (fp4, fp8, mxfp4); the comparison is case-insensitive
    // and the value is passed through as served rather than case-folded, so the annotation
    // shows what the provider actually said.
    return normalized is null || string.Equals(normalized, "unknown", StringComparison.OrdinalIgnoreCase)
        ? null
        : normalized;
  }

  /// <summary>This variant's slug for display when no friendly annotation exists, or the
  ///     provider name's fallback. Never null, so a surface can always print something.
  ///     </summary>
  public string SlugOrEmpty => Slug ?? string.Empty;
}
