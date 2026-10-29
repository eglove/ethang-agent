namespace eThangAgent.AgentDomain;

/// <summary>The chain's ledger slug (issue #302 amendment, D14): every step of a chain reads
///     and writes <c>sci.&lt;slug&gt;/ledger</c>, so the slug belongs to the CHAIN and not to
///     the step that happens to be running.
///     <para>DERIVED, NOT STORED. No record carries a slug column. The chain's title is the
///     only human label a chain has, and the <c>sdd.&lt;slug&gt;</c> precedent already derives
///     its namespace from the record's title. A stored column would be a second answer to a
///     question the title already answers, and it could drift from the title after an edit.</para>
///     <para>PURE BY CONTRACT: the same title always yields the same slug, so the brief
///     (#302), the chain contract in the system prompt (#311), and the telemetry thresholds
///     key (#309) cannot disagree about where a chain's ledger lives. That is why this is a
///     pure function rather than a value each caller mints for itself.</para></summary>
public static class ChainLedgerSlug
{
  /// <summary>The longest slug this produces. A slug is one key segment, and a key that grows
  ///     without bound is a key nobody can retype from a transcript.</summary>
  public const int MaxLength = 64;

  /// <summary>What a chain with no usable title falls back to. Never empty: an empty slug
  ///     would produce the key <c>sci./ledger</c>, which is not a key.</summary>
  public const string Fallback = "chain";

  /// <summary>The ledger slug for <paramref name="title"/>. Total: any string, including
  ///     null, whitespace, or pure punctuation, yields a usable segment.</summary>
  public static string For(string? title) =>
      string.IsNullOrWhiteSpace(title) ? Fallback : Shape(WithoutLeadingDate(title.Trim()));

  /// <summary>Lowercases ASCII letters and digits and collapses every other run to a single
  ///     hyphen, then bounds the length. The same shaping the worktree-name derivation uses,
  ///     minus its uniqueness fragment: a slug that changed per call would be a different
  ///     ledger every time.</summary>
  private static string Shape(string source)
  {
    System.Text.StringBuilder sb = new();
    bool lastWasHyphen = false;
    foreach (char c in source)
    {
      char lower = char.ToLowerInvariant(c);
      if (char.IsAsciiLetterOrDigit(lower))
      {
        _ = sb.Append(lower);
        lastWasHyphen = false;
      }
      else if (!lastWasHyphen && sb.Length > 0)
      {
        _ = sb.Append('-');
        lastWasHyphen = true;
      }
    }

    string slug = sb.ToString().TrimEnd('-');
    return slug.Length == 0
      ? Fallback
      : Bound(slug);
  }

  /// <summary>True when every named position holds an ASCII digit.</summary>
  private static bool DigitsAt(string text, params int[] positions) =>
      positions.All(position => char.IsAsciiDigit(text[position]));

  /// <summary>Bounds a slug to <see cref="MaxLength"/> without leaving a trailing hyphen.</summary>
  private static string Bound(string slug) =>
      slug.Length > MaxLength ? slug[..MaxLength].TrimEnd('-') : slug;

  /// <summary>Drops a leading ISO date (<c>2026-08-24-</c>). A date prefix is a sort key,
  ///     not a name: the <c>sdd.&lt;slug&gt;</c> precedent drops it so the namespace reads as
  ///     the plan's subject rather than as the day it was written.</summary>
  private static string WithoutLeadingDate(string title) =>
      title.Length >= 11 && LooksLikeIsoDate(title) ? title[11..] : title;

  /// <summary>True when the first eleven characters are <c>dddd-dd-dd-</c>. Checked
  ///     positionally rather than by parsing: a title is not a date, and a failed parse is
  ///     not an error worth surfacing.
  ///     <para>The nine digit positions are counted rather than chained so the predicate stays
  ///     one expression: a nine-clause conjunction is the same fact stated nine times.</para></summary>
  private static bool LooksLikeIsoDate(string title) =>
      title[4] == '-' && title[7] == '-' && title[10] == '-'
      && DigitsAt(title, 0, 1, 2, 3) && DigitsAt(title, 5, 6) && DigitsAt(title, 8, 9);
}
