namespace eThangAgent.Composition;

/// <summary>Curated capability facts for the fallback/bootstrap model ids: the session
///     bootstrap runs BEFORE any catalog container exists, so the id -> vision flag
///     question is answered from the same curated source the provider catalogs use for
///     these ids (openrouter/auto: routing can reach multimodal upstreams => true;
///     glm-5.3-flash: the z.ai catalog's flash entry is text-only => false; unknown
///     ids => false, capability is never guessed). The provider catalogs remain the
///     authority once a session's container - and with it the catalog - exists.
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
      "glm-5.3" => true,
      _ => false,
    };
  }
}
