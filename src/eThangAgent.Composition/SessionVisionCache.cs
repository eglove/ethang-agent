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
///     <para>THE STATE IS AN INT rather than a nullable bool so the read is a volatile load: a
///     tool reads it on a pool thread while the UI thread refreshes it.</para></summary>
public sealed class SessionVisionCache(IModelCatalog catalog, SessionModelPreferences preferences)
{
  private const int Unknown = 0;
  private const int Accepts = 1;
  private const int Withholds = 2;

  private readonly IModelCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
  private readonly SessionModelPreferences _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
  private int _state = Unknown;

  /// <summary>The last resolved answer, or null before anything was resolved.</summary>
  public bool? Current => Volatile.Read(ref _state) switch
  {
    Accepts => true,
    Withholds => false,
    _ => null,
  };

  /// <summary>Re-resolves the head model's vision capability from the catalog. A selection
  ///     that names no model clears the answer: nothing is selected, so nothing is known.
  ///     A failed catalog read clears it too, for the same reason.</summary>
  public async Task RefreshAsync(CancellationToken ct = default)
  {
    if (_preferences.ModelIds is not { Count: > 0 } ids)
    {
      Volatile.Write(ref _state, Unknown);
      return;
    }

    string baseModel = ModelVariants.BaseOf(ids[0]);
    Result<IReadOnlyList<ModelProviderEntry>> entries = await _catalog.GetAsync(ct).ConfigureAwait(false);
    Volatile.Write(ref _state, entries.IsSuccess
        && entries.Value.Any(e => string.Equals(e.ModelId, baseModel, StringComparison.Ordinal) && e.SupportsVision)
            ? Accepts
            : Withholds);
  }
}
