using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition;

/// <summary>The session's model vision fact, resolved from the session's own model selection
///     rather than from a running agent. A container that serves no turn - the app's, which
///     hands every turn to a conversation host - never builds a root agent, so a capability
///     answered only from the running agent has no answer there and reports no image input.
///     <para>THE ANSWER IS CACHED, NOT COMPUTED PER CALL: the capability is synchronous and a
///     catalog read is not, so the fact is refreshed when the selection changes and read as a
///     plain value. Null means the question has not been answered yet, which the capability
///     reports as no image input - capability is never guessed.</para>
///     <para>THE CATALOG IS RESOLVED PER VENDOR. The head entry names the vendor that serves it,
///     and a session is not tied to a provider, so one catalog held here would answer for
///     whichever vendor the session opened under and report every other vendor's models as
///     incapable.</para>
///     <para>THE STATE IS AN INT rather than a nullable bool so the read is a volatile load: a
///     tool reads it on a pool thread while the UI thread refreshes it.</para></summary>
public sealed class SessionVisionCache(IModelProviderResolver resolver, SessionModelPreferences preferences)
{
  private const int Unknown = 0;
  private const int Accepts = 1;
  private const int Withholds = 2;

  private readonly IModelProviderResolver _resolver =
      resolver ?? throw new ArgumentNullException(nameof(resolver));
  private readonly SessionModelPreferences _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
  private int _state = Unknown;

  /// <summary>The last resolved answer, or null before anything was resolved.</summary>
  public bool? Current => Volatile.Read(ref _state) switch
  {
    Accepts => true,
    Withholds => false,
    _ => null,
  };

  /// <summary>Re-resolves the head model's vision capability from the catalog of the vendor that
  ///     serves it. A selection that names no model clears the answer: nothing is selected, so
  ///     nothing is known. A failed catalog read clears it too, for the same reason.</summary>
  /// <param name="ct">Cancellation.</param>
  public async Task RefreshAsync(CancellationToken ct = default)
  {
    if (_preferences.Entries is not { Count: > 0 } entries)
    {
      Volatile.Write(ref _state, Unknown);
      return;
    }

    ModelSelectionEntry head = entries[0];
    string baseModel = ModelVariants.BaseOf(head.ModelId);
    Volatile.Write(ref _state, await AcceptsAsync(baseModel, head.Vendor, ct).ConfigureAwait(false)
        ? Accepts
        : Withholds);
  }

  /// <summary>Whether the model accepts image input, asked of the vendor that serves it - or of
  ///     every configured vendor when the entry names none.</summary>
  /// <param name="baseModel">The base model id.</param>
  /// <param name="vendorId">The vendor the entry names, or null.</param>
  /// <param name="ct">Cancellation.</param>
  private async Task<bool> AcceptsAsync(string baseModel, string? vendorId, CancellationToken ct)
  {
    foreach (string vendor in vendorId is { Length: > 0 } named ? [named] : ModelVendors.All)
    {
      Result<IModelCatalog> catalog = _resolver.Catalog(vendor);
      if (!catalog.IsSuccess)
      {
        continue;
      }

      Result<IReadOnlyList<ModelProviderEntry>> entries =
          await catalog.Value.GetAsync(ct).ConfigureAwait(false);
      if (entries.IsSuccess
          && entries.Value.Any(e => string.Equals(e.ModelId, baseModel, StringComparison.Ordinal)
              && e.SupportsVision))
      {
        return true;
      }
    }

    return false;
  }
}
