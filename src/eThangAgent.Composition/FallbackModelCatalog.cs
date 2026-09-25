namespace eThangAgent.Composition;

/// <summary>Curated capability facts for the fallback/bootstrap model id: the session
///     bootstrap runs BEFORE any catalog container exists, so the id -> vision flag
///     question is answered from the same curated source the provider catalog uses for
///     this id (openrouter/auto: routing can reach multimodal upstreams => true;
///     unknown ids => false, capability is never guessed). The provider catalog remains
///     the authority once a session's container - and with it the catalog - exists.
///     Pinned by tests.</summary>
public static class FallbackModelCatalog
{
  /// <summary>True when the named fallback/bootstrap model id accepts image input
  ///     under the provider's published capability facts.</summary>
  public static bool AcceptsImageInput(string modelId)
  {
    return modelId switch
    {
      Providers.RoutingModelId => true,
      _ => false,
    };
  }
}
