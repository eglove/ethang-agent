namespace eThangAgent.ModelDomain;

/// <summary>The three routing variants OpenRouter offers, each expressed as a SUFFIX on the
///     model id rather than as a routing object (see
///     <see href="https://openrouter.ai/docs/guides/routing/model-variants/overview"/>).
///     <para>WHY THIS REPLACES THE PROVIDER PICKER: the provider/endpoint surface asked the
///     user to compare dozens of rows on price, latency, uptime, and cache economics before a
///     single turn ran — a decision the harness had already measured and could rank. What the
///     user actually wants to say is the one thing measurement cannot infer: <em>give me speed
///     when it matters</em> or <em>give me the floor price</em>. OpenRouter publishes exactly
///     those two intents as routing variants, so the decision collapses to three choices and
///     the wire carries it in the model id itself.</para>
///     <para>THE SUFFIX IS PURE ROUTING, NOT A PROVIDER PIN: <c>:nitro</c> sorts the model's
///     whole eligible pool by throughput and admits priority-tier endpoints; <c>:floor</c>
///     sorts by price and admits flex-tier endpoints. Neither narrows the pool to one
///     provider, so a variant can never make a model unservable — which is why the retired
///     <c>UnknownPinnedProvider</c> failure has nothing left to check.</para>
///     <para>BILLING FOLLOWS THE TIER ACTUALLY SERVED: a nitro request served on a priority
///     endpoint is charged priority rates, and the usage ledger records those prices because
///     the response names the model that served. That is why the effective-pricing and cache
///     statistics survive this simplification unchanged.</para></summary>
public enum ModelVariant
{
  /// <summary>No suffix: the provider's own default routing (the pre-existing behaviour).</summary>
  Standard,

  /// <summary><c>:nitro</c> — sorts endpoints by throughput, admitting priority-tier
  ///     endpoints, which bill at priority rates.</summary>
  Nitro,

  /// <summary><c>:floor</c> — sorts endpoints by price, admitting flex-tier endpoints, which
  ///     bill at their discounted rate.</summary>
  Floor,
}

/// <summary>The variant's wire syntax and presentation, kept as PURE functions beside the enum
///     so the resolver, the picker, the status line, and the persistence codec cannot each
///     invent their own spelling of the same suffix.
///     <para>ONE-TO-ONE WITH THE DOCUMENTED SURFACE: only the two suffixes OpenRouter
///     documents are recognised. A colon that appears in a real catalog id for another reason
///     (<c>vendor/model:free</c> is a PRICING suffix, not a routing variant) is part of the
///     model id and is never consumed — stripping it would silently retarget the request to a
///     different product.</para></summary>
public static class ModelVariants
{
  private const string NitroSuffix = ":nitro";
  private const string FloorSuffix = ":floor";

  /// <summary>The variants in the order the picker offers them: the default first, then the
  ///     two the user actually chooses between.</summary>
  public static IReadOnlyList<ModelVariant> All { get; } =
      [ModelVariant.Standard, ModelVariant.Nitro, ModelVariant.Floor];

  /// <summary>The suffix OpenRouter documents for this variant: empty for
  ///     <see cref="ModelVariant.Standard"/>, whose wire form is the bare model id.</summary>
  public static string Suffix(ModelVariant variant) => variant switch
  {
    ModelVariant.Standard => string.Empty,
    ModelVariant.Nitro => NitroSuffix,
    ModelVariant.Floor => FloorSuffix,
    _ => throw Unhandled(variant),
  };

  /// <summary>The model id to put on the wire for this variant.
  ///     <para>IDEMPOTENT AND REPLACING: a variant suffix already on the id is stripped first,
  ///     so re-applying cannot double it (<c>model:nitro:nitro</c>) and switching variants
  ///     replaces the previous one instead of stacking on it — the resolver applies this on
  ///     every resolution, so both properties are load-bearing rather than cosmetic.</para>
  ///     </summary>
  public static string ApplyTo(ModelVariant variant, string modelId)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
    return BaseOf(modelId) + Suffix(variant);
  }

  /// <summary>The bare catalog model an id refers to: the id with any ROUTING VARIANT suffix
  ///     removed, everything else preserved.
  ///     <para>Every catalog lookup strips first: the catalog is keyed by the real model
  ///     (<c>alpha/model</c>) while the wire carries the routing intent
  ///     (<c>alpha/model:nitro</c>). Looking either up on the other's key finds nothing — an
  ///     unknown context window fails the turn, and an unpriced model silently loses its
  ///     cost.</para></summary>
  public static string BaseOf(string modelId)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
    return FromModelId(modelId) switch
    {
      ModelVariant.Standard => modelId,
      ModelVariant.Nitro => modelId[..^NitroSuffix.Length],
      ModelVariant.Floor => modelId[..^FloorSuffix.Length],
      _ => modelId,
    };
  }

  /// <summary>The variant an id carries, or <see cref="ModelVariant.Standard"/> when it
  ///     carries none. Only a TRAILING documented suffix counts.</summary>
  public static ModelVariant FromModelId(string modelId)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
    return modelId switch
    {
      _ when modelId.EndsWith(NitroSuffix, StringComparison.Ordinal) => ModelVariant.Nitro,
      _ when modelId.EndsWith(FloorSuffix, StringComparison.Ordinal) => ModelVariant.Floor,
      _ => ModelVariant.Standard,
    };
  }

  /// <summary>The dropdown label AND the status-line name: the variant's bare name.
  ///     <para>DELIBERATELY SHORT. The control that offers these three choices carries the
  ///     explanation underneath it, so repeating the tradeoff in every row and in the status
  ///     bar only lengthened the decision without adding to it.</para></summary>
  public static string Describe(ModelVariant variant) => variant switch
  {
    ModelVariant.Standard => "Standard",
    ModelVariant.Nitro => "Nitro",
    ModelVariant.Floor => "Floor",
    _ => throw Unhandled(variant),
  };

  /// <summary>The refusal for a member this build does not know. Written once so both switches
  ///     state the same consequence: an unhandled variant would silently put a request on the
  ///     wire with the WRONG routing intent, which is worse than failing.</summary>
  private static ArgumentOutOfRangeException Unhandled(ModelVariant variant)
      => new(nameof(variant), variant,
          "Every variant must state its own wire suffix and label; an unhandled member would "
          + "silently send a routing intent the user did not choose.");
}
