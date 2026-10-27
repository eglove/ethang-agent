using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

/// <summary>The rule that a pinned provider must be one the catalog still offers for the
///     chosen model (issue #297, D5), as ONE pure function so the root resolver and the child
///     spawner cannot disagree about when a pin has vanished.
///     <para>WHY A NAMED FAILURE AND NOT A FALLBACK: the pin is a hard constraint (D2). A
///     provider that has disappeared from the catalog cannot serve the request, and serving it
///     from another provider is exactly the silent substitution this harness refuses — the
///     user's cost, cache ratio, and reliability expectations were all chosen for the provider
///     that is now gone.</para>
///     <para>WHAT IS *NOT* A VANISHED PROVIDER: an unreadable catalog (null entries) and an
///     EMPTY catalog. Both are the shapes a cold or failed fetch leaves behind, and treating
///     them as 'nothing exists' would fail every pinned turn on a fresh start — a transient
///     fault reported as a permanent fact about the user's choice.</para></summary>
public static class PinnedProvider
{
  /// <summary>The named failure when <paramref name="providerName"/> is pinned but no catalog
  ///     entry pairs it with <paramref name="modelId"/>; null when the pin is servable, unset,
  ///     or unverifiable.
  ///     <para>AN UNKNOWN MODEL IS NOT THIS CHECK'S FAILURE: when the catalog lists no entry for
  ///     the model at all, the window lookup is the authority that reports it, and saying 'your
  ///     provider no longer offers it' would name the wrong half of the pair.</para></summary>
  public static DomainError? MissingFor(IReadOnlyList<ModelProviderEntry>? entries, string modelId, string? providerName)
  {
    if (string.IsNullOrWhiteSpace(providerName) || entries is null || entries.Count == 0)
    {
      return null;
    }

    bool modelKnown = entries.Any(e => e.ModelId == modelId);
    bool providerServesModel = entries.Any(e => e.ModelId == modelId && e.ProviderName == providerName);
    return !modelKnown || providerServesModel
        ? null
        : new DomainError("UnknownPinnedProvider",
        $"the pinned provider '{providerName}' no longer offers model '{modelId}' in the catalog, "
        + "so the turn cannot be served by the provider you chose; the harness never substitutes another provider. "
        + "Pick the model again in Model Settings (or clear the provider choice) and send the turn again.");
  }
}
