namespace eThangAgent.ComputerUse.Host;

/// <summary>The slice of a walk the model is shown, and what it was not shown. A walk
///     collects every element; this decides which contiguous run of it travels, so a
///     tree larger than one answer is reachable across successive observations instead
///     of being silently cut at a fixed ceiling.</summary>
public sealed record UiaWalkWindow
{
  /// <summary>How many elements one observation shows when the caller names no limit.</summary>
  public const int DefaultLimit = 400;

  /// <summary>The largest limit a caller may name. A model asking for more than this is
  ///     asking for an answer it cannot act on; the request is clamped and the clamp is
  ///     reported rather than applied silently.</summary>
  public const int MaxLimit = 2000;

  /// <summary>The first element index in this window.</summary>
  public required int Offset { get; init; }

  /// <summary>How many elements this window carries.</summary>
  public required int Shown { get; init; }

  /// <summary>How many elements the whole walk collected.</summary>
  public required int Total { get; init; }

  /// <summary>True when elements exist before this window.</summary>
  public bool HasPrevious => Offset > 0;

  /// <summary>True when elements exist after this window.</summary>
  public bool HasMore => Offset + Shown < Total;

  /// <summary>The index a follow-up observation starts at, or null when nothing is left.</summary>
  public int? NextOffset => HasMore ? Offset + Shown : null;

  /// <summary>True when the walk was cut short of the whole tree, so the caller knows the
  ///     total is a floor rather than a count.</summary>
  public bool Truncated { get; init; }

  /// <summary>Selects the window over a walk of <paramref name="total"/> elements. An
  ///     offset past the end yields an empty window positioned at the end rather than an
  ///     error: the caller asked for a page that does not exist, and the honest answer is
  ///     that there is nothing there and where the last page starts.</summary>
  public static UiaWalkWindow Select(int total, int offset, int limit)
  {
    ArgumentOutOfRangeException.ThrowIfNegative(total);
    ArgumentOutOfRangeException.ThrowIfNegative(offset);
    ArgumentOutOfRangeException.ThrowIfNegative(limit);

    int boundedLimit = Math.Clamp(limit, 1, MaxLimit);
    int start = Math.Min(offset, total);
    int shown = Math.Min(boundedLimit, total - start);
    return new UiaWalkWindow { Offset = start, Shown = shown, Total = total };
  }

  /// <summary>The model-facing note describing this window, or null when the whole tree
  ///     travelled and there is nothing to disclose. The note always names the total and
  ///     the next offset, because a model that cannot see the rest must be able to ask
  ///     for it by number.</summary>
  public string? Note()
  {
    if (!Truncated && !HasPrevious && !HasMore)
    {
      return null;
    }

    string totalText = Truncated ? $"at least {Total}" : Total.ToString(System.Globalization.CultureInfo.InvariantCulture);
    string range = Shown == 0
      ? $"no elements at offset {Offset}"
      : $"elements {Offset}-{Offset + Shown - 1} of {totalText}";
    string next = NextOffset is { } n
      ? $" Next page: observe with offset={n}."
      : string.Empty;
    string previous = HasPrevious ? $" Previous page: offset={Math.Max(0, Offset - Shown)}." : string.Empty;
    return $"note: showing {range}.{next}{previous}";
  }
}
