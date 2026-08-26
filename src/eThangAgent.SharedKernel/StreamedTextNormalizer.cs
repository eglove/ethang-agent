using System.Text;

namespace eThangAgent.SharedKernel;

/// <summary>
///     Incrementally normalizes streamed model reasoning text for display. Providers emit
///     reasoning as tiny fragments full of hard wraps and blank-line runs; this joins wraps
///     between two letters (the model hard-wraps inside CamelCase identifiers constantly),
///     attaches wraps before closing/comma punctuation directly, preserves sentence,
///     heading-after-colon, and bullet breaks, collapses blank-line floods to a single
///     blank line, and drops leading and trailing breaks. Presentation-only: never applied
///     to text persisted in a conversation.
/// </summary>
public sealed class StreamedTextNormalizer
{
  private readonly StringBuilder _text = new();

  // Line breaks seen since the last non-break character; resolved against the character
  // that ends the run, because a break's meaning depends on both neighbors.
  private int _pendingBreaks;

  public void Append(string delta)
  {
    ArgumentNullException.ThrowIfNull(delta);
    foreach (char ch in delta)
    {
      if (ch == '\r')
      {
        continue;
      }

      if (ch == '\n')
      {
        _pendingBreaks++;
        continue;
      }
      if (_pendingBreaks > 0)
      {
        EmitBreak(_pendingBreaks, ch);
        _pendingBreaks = 0;
      }
      _ = _text.Append(ch);
    }
  }

  /// <summary>The normalized text so far, with trailing line breaks trimmed.</summary>
  public string Text => _text.ToString().TrimEnd('\n');

  private void EmitBreak(int count, char next)
  {
    if (_text.Length == 0)
    {
      return; // leading breaks are dropped
    }

    char prev = _text[^1];

    // Two or more breaks mean paragraph separation: collapse to exactly one blank line.
    if (count >= 2)
    {
      _ = _text.Append("\n\n");
      return;
    }

    // Bullet and list markers always start a line.
    if (next is '-' or '*' or '\u2022' or '>')
    {
      _ = _text.Append('\n');
      return;
    }

    // Sentence-ending punctuation keeps its break.
    if (prev is '.' or '!' or '?' or '\u2026')
    {
      _ = _text.Append('\n');
      return;
    }

    // A bare wrap between two letters is mid-word — including CamelCase identifiers,
    // which code-dense reasoning emits constantly — so join regardless of case. An
    // unpunctuated sentence boundary before a capital joins too: identifier wraps
    // vastly outnumber it, and a wrong join reads cheaper than a wrong break.
    if (char.IsLetter(prev) && char.IsLetter(next))
    {
      return;
    }

    // Closing and comma punctuation attaches to the previous word: join directly.
    if (next is ',' or '.' or ';' or ':' or ')')
    {
      return;
    }

    // A capital after a non-letter (digit, bracket) starts a heading or list entry.
    if (char.IsUpper(next))
    {
      _ = _text.Append('\n');
      return;
    }

    // Everything else (opening brackets, clause wraps after punctuation) joins with a space.
    _ = _text.Append(' ');
  }
}
