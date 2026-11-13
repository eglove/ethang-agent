namespace eThangAgent.ModelDomain;

/// <summary>One endpoint the provider states is eligible under a retention tier, keyed by
///     <see cref="ModelProviderEntry.Key"/>.
///     <para>THE ROW CARRIES WHICH TIER, NOT ONLY THAT IT IS ELIGIBLE. The provider publishes
///     some endpoints as eligible only under safety retention: they may keep prompts to
///     prevent abuse and never train on them. That is a weaker promise than the strict tier,
///     so it travels on the row rather than being flattened into membership - a surface that
///     showed such an endpoint as plainly zero-retention would state a promise it does not
///     make.</para></summary>
/// <param name="ModelId">The model the endpoint serves.</param>
/// <param name="ProviderName">The provider's name for the endpoint, as the catalog spells
///     it.</param>
/// <param name="Slug">The routable endpoint slug (<c>tag</c>), or null when the provider
///     named none.</param>
/// <param name="RequiresSafetyRetention">True when the endpoint is eligible only under
///     <see cref="RetentionPolicy.SafetyRetention"/>.</param>
public sealed record RetentionEligibleEndpoint(
    string ModelId,
    string? ProviderName,
    string? Slug,
    bool RequiresSafetyRetention)
{
  /// <summary>The catalog key this endpoint is, built by the same rule
  ///     <see cref="ModelProviderEntry.Key"/> uses so an eligible row is matched by exactly
  ///     the identity the picker shows and a pin sends.</summary>
  public string Key => ModelProviderEntry.KeyOf(ModelId, ProviderName, Slug);
}

/// <summary>The endpoints the provider states are eligible under a retention tier.
///     <para>THIS LIST IS THE AUTHORITY, NOT THE SLUG. OpenRouter publishes the eligible set
///     at <c>/api/v1/endpoints/zdr</c> and describes it as the complete set for a
///     ZDR-enforced request; a <c>zdr</c> segment in an endpoint's tag is one way an endpoint
///     gets there, not the mark of membership. Measured live 2026-11-01,
///     <c>deepseek/deepseek-v4.1-flash-1017</c> has 42 eligible endpoints and not one of their
///     tags says <c>zdr</c> - reading the slug alone therefore reported an eligible model as
///     ineligible and refused its turns.</para>
///     <para>SAFETY-RETENTION ENDPOINTS ARE IN THE LIST TOO, because the provider treats them
///     as eligible. Excluding them here would refuse a model the provider would serve; the
///     difference is carried on the row and applied by the tier the caller asks about.</para>
/// </summary>
/// <param name="Endpoints">The eligible endpoints, in the order the provider published
///     them.</param>
public sealed record RetentionEligibleList(IReadOnlyList<RetentionEligibleEndpoint> Endpoints)
{
  /// <summary>The list as fetched, or empty when the provider named nothing.</summary>
  public static RetentionEligibleList Empty { get; } = new([]);

  /// <summary>The list built from the endpoints the provider published.</summary>
  public static RetentionEligibleList Of(IEnumerable<RetentionEligibleEndpoint> endpoints)
  {
    ArgumentNullException.ThrowIfNull(endpoints);
    return new RetentionEligibleList([.. endpoints]);
  }

  /// <summary>True when the provider names at least one endpoint for
  ///     <paramref name="modelId"/> that the stated tier admits.
  ///     <para>THE TIER DECIDES WHICH ROWS COUNT. <see cref="RetentionPolicy.ZeroDataRetention"/>
  ///     counts only endpoints that do not require safety retention, because a strict request
  ///     must not be served by one that may keep the prompt;
  ///     <see cref="RetentionPolicy.SafetyRetention"/> counts any of them, because that tier
  ///     accepts the weaker promise; and
  ///     <see cref="RetentionPolicy.RetentionAndTraining"/> places no restriction, so it is
  ///     always servable and the list has nothing to prove.</para>
  ///     <para>A model absent from the list has no eligible endpoint under an enforced tier,
  ///     which is the fact a restricted session is refused on.</para></summary>
  public bool ServesModel(string modelId, RetentionPolicy policy)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
    return !RetentionPolicies.IsEnforced(policy)
        || Endpoints.Any(endpoint => string.Equals(endpoint.ModelId, modelId, StringComparison.Ordinal)
            && Admits(endpoint, policy));
  }

  /// <summary>The distinct non-empty slugs the provider names for <paramref name="modelId"/>
  ///     under <paramref name="policy"/>, in the order the list holds them.
  ///     <para>THESE ARE WHAT A STRICT REQUEST PINS: the provider decides whether a
  ///     safety-retention endpoint may serve a ZDR-enforced request from the account setting
  ///     rather than from the request, so naming the admitted slugs is the only way the
  ///     harness can exclude the others.</para>
  ///     <para>A NULL SLUG IS OMITTED rather than invented: an endpoint the provider gave no
  ///     tag for is not addressable, and a pin naming it would fail the whole request.</para>
  ///     </summary>
  public IReadOnlyList<string> SlugsFor(string modelId, RetentionPolicy policy)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
    if (!RetentionPolicies.IsEnforced(policy))
    {
      return [];
    }

    List<string> slugs = [];
    foreach (RetentionEligibleEndpoint endpoint in Endpoints)
    {
      if (string.Equals(endpoint.ModelId, modelId, StringComparison.Ordinal)
          && Admits(endpoint, policy)
          && endpoint.Slug is { Length: > 0 } slug
          && !slugs.Contains(slug, StringComparer.Ordinal))
      {
        slugs.Add(slug);
      }
    }

    return slugs;
  }

  /// <summary>True when this endpoint is on the list and the stated tier admits it.</summary>
  public bool IsEligible(string modelId, string? providerName, string? slug, RetentionPolicy policy)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
    if (!RetentionPolicies.IsEnforced(policy))
    {
      return true;
    }

    string key = ModelProviderEntry.KeyOf(modelId, providerName, slug);
    return Endpoints.Any(endpoint =>
        string.Equals(endpoint.Key, key, StringComparison.Ordinal) && Admits(endpoint, policy));
  }

  /// <summary>True when the stated tier accepts this row: a strict tier excludes the
  ///     safety-retention ones, and every other enforced tier accepts them.</summary>
  private static bool Admits(RetentionEligibleEndpoint endpoint, RetentionPolicy policy)
      => policy != RetentionPolicy.ZeroDataRetention || !endpoint.RequiresSafetyRetention;
}
