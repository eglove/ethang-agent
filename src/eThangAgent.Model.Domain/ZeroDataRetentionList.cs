namespace eThangAgent.ModelDomain;

/// <summary>The endpoints the provider states are eligible when zero data retention is
///     required, keyed by <see cref="ModelProviderEntry.Key"/>.
///     <para>THIS LIST IS THE AUTHORITY, NOT THE SLUG. OpenRouter publishes the eligible set at
///     <c>/api/v1/endpoints/zdr</c> and describes it as the complete set for a ZDR-enforced
///     request; a <c>zdr</c> segment in an endpoint's tag is one way an endpoint gets there, not
///     the mark of membership. Measured live 2026-11-01, <c>deepseek/deepseek-v4.1-flash-1017</c>
///     has 42 eligible endpoints and not one of their tags says <c>zdr</c> - reading the slug
///     alone therefore reported an eligible model as ineligible and refused its turns.</para>
///     <para>SAFETY-RETENTION ENDPOINTS ARE IN THE LIST TOO, because the provider treats them
///     as eligible: they may keep prompts to prevent abuse and never train on them. They are
///     eligible, so they are members; the difference is carried on the row
///     (<see cref="EndpointVariant.RequiresSafetyRetention"/>) rather than by excluding them
///     here, because excluding them would refuse a model that the provider would serve.</para>
/// </summary>
/// <param name="EndpointKeys">The eligible endpoints, as <see cref="ModelProviderEntry.Key"/>
///     values.</param>
public sealed record ZeroDataRetentionList(IReadOnlyList<string> EndpointKeys)
{
  private readonly HashSet<string> _keys = new(EndpointKeys, StringComparer.Ordinal);

  /// <summary>The list as fetched, or empty when the provider named nothing.</summary>
  public static ZeroDataRetentionList Empty { get; } = new([]);

  /// <summary>The list built from the keys the provider published.</summary>
  public static ZeroDataRetentionList Of(IEnumerable<string> keys)
  {
    ArgumentNullException.ThrowIfNull(keys);
    return new ZeroDataRetentionList([.. keys]);
  }

  /// <summary>True when the provider names at least one eligible endpoint for
  ///     <paramref name="modelId"/>. A model absent from the list has no eligible endpoint,
  ///     which is the fact a restricted session is refused on.</summary>
  public bool ServesModel(string modelId)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
    string prefix = modelId + ":";
    return _keys.Any(key => key.StartsWith(prefix, StringComparison.Ordinal));
  }

  /// <summary>True when this endpoint is on the list.</summary>
  public bool IsEligible(string modelId, string? providerName, string? slug)
      => _keys.Contains(ModelProviderEntry.KeyOf(modelId, providerName, slug));
}
