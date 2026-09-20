using eThangAgent.SharedKernel;

namespace eThangAgent.ConversationDomain;

/// <summary>A parsed message selection: either the last N messages or a 1-based
///     inclusive position range S..E. Strict at the door — anything else is a typed
///     error, never coerced. Positions resolve against the CURRENT list length, so
///     every selection names its messages relative to the render the model read.</summary>
public sealed record ContextSelection
{
  private const string LastPrefix = "last ";

  private ContextSelection(int? lastCount, int? start, int? end)
  {
    LastCount = lastCount;
    Start = start;
    End = end;
  }

  /// <summary>Message count of a 'last N' selection; null for a range selection.</summary>
  public int? LastCount { get; }

  /// <summary>1-based inclusive start of a range selection; null for 'last N'.</summary>
  public int? Start { get; }

  /// <summary>1-based inclusive end of a range selection; null for 'last N'.</summary>
  public int? End { get; }

  /// <summary>Parses strictly: 'last N' (N >= 0) or 'S..E' (1 &lt;= S &lt;= E). Both bound to
  ///     int range. Any other text fails InvalidSelection — no guessing.</summary>
  public static Result<ContextSelection> Parse(string? text)
  {
    if (string.IsNullOrWhiteSpace(text))
    {
      return Fail("selection is required.");
    }

    string trimmed = text.Trim();
    if (trimmed.StartsWith(LastPrefix, StringComparison.Ordinal))
    {
      string countText = trimmed[LastPrefix.Length..];
      return int.TryParse(countText, out int count) && count >= 0 && count != int.MaxValue
          ? Result.Success(new ContextSelection(count, null, null))
          : Fail($"'last N' needs a whole number N >= 0, but got '{countText}'.");
    }

    int separator = trimmed.IndexOf("..", StringComparison.Ordinal);
    if (separator > 0)
    {
      string startText = trimmed[..separator];
      string endText = trimmed[(separator + 2)..];
      bool parsedStart = int.TryParse(startText, out int start);
      bool parsedEnd = int.TryParse(endText, out int end);
      bool ok = parsedStart && parsedEnd && start >= 1 && start <= end;
      return ok
          ? Result.Success(new ContextSelection(null, start, end))
          : Fail($"range selection needs 'S..E' with 1 <= S <= E, but got '{trimmed}'.");
    }

    // Bare position: exactly one message ('3' means '3..3').
    return int.TryParse(trimmed, out int single) && single >= 1
        ? Result.Success(new ContextSelection(null, single, single))
        : Fail($"selection must be 'last N', 'S..E', or a position, but got '{trimmed}'.");
  }

  /// <summary>Resolves the selection against a list length: ascending 1-based positions
  ///     mapped to 0-based indexes. A range extending past the end fails
  ///     PositionOutOfRange — clamping would silently remove messages the model did
  ///     not name.</summary>
  public Result<IReadOnlyList<int>> Resolve(int listLength)
  {
    if (LastCount is { } count)
    {
      int effective = Math.Min(count, listLength);
      int[] indexes = new int[effective];
      for (int i = 0; i < effective; i++)
      {
        indexes[i] = listLength - effective + i;
      }

      return Result.Success<IReadOnlyList<int>>(indexes);
    }

    int start = Start!.Value;
    int end = End!.Value;
    if (end > listLength)
    {
      return Result.Failure<IReadOnlyList<int>>(new DomainError("PositionOutOfRange",
          $"Selection {Start}..{End} extends past the conversation end ({listLength} message(s))."));
    }

    int[] range = new int[end - start + 1];
    for (int i = 0; i < range.Length; i++)
    {
      range[i] = start - 1 + i;
    }

    return Result.Success<IReadOnlyList<int>>(range);
  }

  /// <summary>Canonical render for notices and results.</summary>
  public string Render() => LastCount is { } count ? $"last {count}" : $"{Start}..{End}";

  private static Result<ContextSelection> Fail(string message)
      => Result.Failure<ContextSelection>(new DomainError("InvalidSelection", message));
}
