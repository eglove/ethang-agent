namespace eThangAgent.Desktop.ViewModels;

/// <summary>The model ids a session has dropped because the model refused or rate-limited a
///     turn, and the surviving list every later turn is built from.
///     <para>The object holds ids and nothing else: it has no persisted form, so the stored
///     preference keeps the list the user picked and a session opened again starts from that
///     whole list. One instance belongs to one session.</para></summary>
internal sealed class SessionModelDrops
{
  private readonly List<string> _dropped = [];

  /// <summary>How many models this session has dropped.</summary>
  public int Count => _dropped.Count;

  /// <summary>The stored list minus every dropped id, in the stored list's own order.</summary>
  public IReadOnlyList<string> Surviving(IReadOnlyList<string> stored)
  {
    ArgumentNullException.ThrowIfNull(stored);
    return _dropped.Count == 0
        ? stored
        : [.. stored.Where(id => !_dropped.Contains(id, StringComparer.Ordinal))];
  }

  /// <summary>Drops one model for the rest of this session. Returns true when the id was not
  ///     already dropped, so a caller drops each model at most once.</summary>
  public bool Drop(string modelId)
  {
    if (string.IsNullOrWhiteSpace(modelId) || _dropped.Contains(modelId, StringComparer.Ordinal))
    {
      return false;
    }

    _dropped.Add(modelId);
    return true;
  }

  /// <summary>True when this session has dropped <paramref name="modelId"/>.</summary>
  public bool IsDropped(string modelId) => _dropped.Contains(modelId, StringComparer.Ordinal);

  /// <summary>Clears every drop: a freshly picked list is the user's whole list again.</summary>
  public void Clear() => _dropped.Clear();
}
