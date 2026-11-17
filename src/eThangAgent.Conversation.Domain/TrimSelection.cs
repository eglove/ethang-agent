using eThangAgent.SharedKernel;

namespace eThangAgent.ConversationDomain;

/// <summary>A parsed trim selection: explicit tool-result ordinals ('r3', 'r3,r7',
///     'r3-r12'), the last N tool results ('last N'), or the default (every trimmable
///     result outside the recent tail). Ordinals are 1-based over ALL tool results in
///     the conversation, so an ordinal keeps naming the same result as results are
///     trimmed. Strict at the door: anything else is a typed error, never coerced.</summary>
public sealed record TrimSelection
{
  /// <summary>How many of the most recent tool results the default selection leaves
  ///     alone: the model's working set.</summary>
  public const int DefaultProtectedTail = 5;

  private const string LastPrefix = "last ";

  private TrimSelection(int? lastCount, IReadOnlyList<int>? ordinals)
  {
    LastCount = lastCount;
    Ordinals = ordinals;
  }

  /// <summary>Count of the 'last N' form; null for the default and ordinal forms.</summary>
  public int? LastCount { get; }

  /// <summary>Ascending 1-based tool-result ordinals; null for the default and 'last N' forms.</summary>
  public IReadOnlyList<int>? Ordinals { get; }

  /// <summary>The selection an omitted 'selection' means: every trimmable result
  ///     outside the recent tail.</summary>
  public static TrimSelection Default => new(null, null);

  /// <summary>Parses strictly: empty means the default, 'last N' (N &gt;= 1), or a
  ///     comma-separated list of 'rN' / 'rN-rM' items. The 'r' prefix is required -
  ///     a bare number is a message position, and accepting it here would silently
  ///     trim the wrong result.</summary>
  public static Result<TrimSelection> Parse(string? text)
  {
    if (string.IsNullOrWhiteSpace(text))
    {
      return Result.Success(Default);
    }

    string trimmed = text.Trim();
    if (trimmed.StartsWith(LastPrefix, StringComparison.Ordinal))
    {
      string countText = trimmed[LastPrefix.Length..];
      return int.TryParse(countText, out int count) && count >= 1 && count != int.MaxValue
          ? Result.Success(new TrimSelection(count, null))
          : Fail($"'last N' needs a whole number N >= 1, but got '{countText}'.");
    }

    return ParseOrdinals(trimmed);
  }

  private static Result<TrimSelection> ParseOrdinals(string text)
  {
    List<int> ordinals = [];
    foreach (string raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
      Result<IReadOnlyList<int>> item = ParseOrdinalItem(raw);
      if (!item.IsSuccess)
      {
        return Result.Failure<TrimSelection>(item.Error);
      }

      ordinals.AddRange(item.Value);
    }

    return ordinals.Count == 0
        ? Fail("selection names no tool results.")
        : Result.Success(new TrimSelection(null, [.. ordinals.Distinct().Order()]));
  }

  /// <summary>One comma-separated item: 'rN' or 'rS-rE'. The 'r' prefix is required,
  ///     so a bare number can never be mistaken for a message position.</summary>
  private static Result<IReadOnlyList<int>> ParseOrdinalItem(string raw)
  {
    if (raw.Length < 2 || raw[0] is not ('r' or 'R'))
    {
      return Result.Failure<IReadOnlyList<int>>(new DomainError("InvalidSelection",
          $"'{raw}' is not a tool-result ordinal. Use 'rN' (run List to see them), 'last N', or omit selection."));
    }

    string body = raw[1..];
    int dash = body.IndexOf('-', StringComparison.Ordinal);
    return dash < 0 ? ParseSingle(raw, body) : ParseRange(raw, body, dash);
  }

  private static Result<IReadOnlyList<int>> ParseSingle(string raw, string body)
  {
    return int.TryParse(body, out int single) && single >= 1
        ? Result.Success<IReadOnlyList<int>>([single])
        : Result.Failure<IReadOnlyList<int>>(new DomainError("InvalidSelection",
            $"'{raw}' is not a tool-result ordinal. Use 'rN' with N >= 1."));
  }

  private static Result<IReadOnlyList<int>> ParseRange(string raw, string body, int dash)
  {
    bool okStart = int.TryParse(body[..dash], out int start);
    // The end of 'rS-rE' carries its own 'r'; accept both 'rS-rE' and 'rS-E'.
    string endText = body[(dash + 1)..].TrimStart('r', 'R');
    bool okEnd = int.TryParse(endText, out int end);
    if (!okStart || !okEnd || start < 1 || end < start)
    {
      return Result.Failure<IReadOnlyList<int>>(new DomainError("InvalidSelection",
          $"'{raw}' is not a tool-result range. Use 'rS-rE' with 1 <= S <= E."));
    }

    List<int> range = [];
    for (int i = start; i <= end; i++)
    {
      range.Add(i);
    }

    return Result.Success<IReadOnlyList<int>>(range);
  }

  /// <summary>Canonical render for notices and results.</summary>
  public string Render() => LastCount is { } count
      ? $"last {count}"
      : RenderOrdinals();

  private string RenderOrdinals() => Ordinals is { } ordinals
      ? string.Join(",", ordinals.Select(o => $"r{o}"))
      : "every trimmable result outside the recent tail";

  private static Result<TrimSelection> Fail(string message)
      => Result.Failure<TrimSelection>(new DomainError("InvalidSelection", message));
}
