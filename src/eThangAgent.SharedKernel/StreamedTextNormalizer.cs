using System.Text;

namespace eThangAgent.SharedKernel;

/// <summary>
///     Incrementally normalizes streamed model reasoning text for display. Providers emit
///     reasoning as tiny fragments full of hard wraps and blank-line runs; this joins
///     mid-word wraps, turns comma-style wraps into spaces, preserves sentence, colon, and
///     bullet breaks, collapses blank-line floods to a single blank line, and drops leading
///     and trailing breaks. Presentation-only: never applied to text persisted in a
///     conversation.
/// </summary>
public sealed class StreamedTextNormalizer
{
    private readonly StringBuilder _text = new();

    // Line breaks seen since the last non-break character; resolved against the character
    // that ends the run, because a break's meaning depends on both neighbors.
    private int _pendingBreaks;

    public void Append(string delta)
    {
        foreach (var ch in delta)
        {
            if (ch == '\r')
                continue;
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
            _text.Append(ch);
        }
    }

    /// <summary>The normalized text so far, with trailing line breaks trimmed.</summary>
    public string Text => _text.ToString().TrimEnd('\n');

    private void EmitBreak(int count, char next)
    {
        if (_text.Length == 0)
            return; // leading breaks are dropped

        var prev = _text[^1];

        // Two or more breaks mean paragraph separation: collapse to exactly one blank line.
        if (count >= 2)
        {
            _text.Append("\n\n");
            return;
        }

        // Single break: a hard wrap inside a word joins directly. An uppercase letter
        // after the break reads as a new sentence and keeps its break.
        if (char.IsLetter(prev) && char.IsLower(next))
            return;

        // Sentence-ending punctuation keeps its break.
        if (prev is '.' or '!' or '?' or '\u2026' or ':' || next is '-' or '*' or '\u2022' or '>')
        {
            _text.Append('\n');
            return;
        }

        // A capital after a bare break starts a new sentence or heading.
        if (char.IsUpper(next))
        {
            _text.Append('\n');
            return;
        }

        // Everything else (comma wraps, clause wraps) reads better joined with a space.
        _text.Append(' ');
    }
}
