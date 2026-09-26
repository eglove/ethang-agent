using System.Text;

namespace eThangAgent.SharedKernel;

/// <summary>
///     Incrementally normalizes streamed model reasoning text for display. Providers emit
///     reasoning as tiny fragments full of hard wraps and blank-line runs; this joins wraps
///     between two letters (the model hard-wraps inside CamelCase identifiers constantly),
///     attaches wraps before closing/comma punctuation directly, preserves sentence,
///     heading-after-colon, and bullet breaks, collapses blank-line floods to a single
///     blank line, and drops leading and trailing breaks. Markdown code fences are exempt:
///     from an opening fence line (up to three leading spaces, then three or more backticks
///     or tildes) through the closing fence line, all line breaks are preserved verbatim and
///     no join rules apply — the stream-time litter mode (a wrap between the fence info
///     string and the first code token gluing them into one word, breaking the renderer)
///     cannot occur, and code content survives exactly as emitted. Fence openers and list
///     markers that the model glues to the end of a prose line are repaired: a mid-line
///     marker run of three or more, and a short digit run followed by dot+space after a
///     space, force a break so the fence/list starts its own line (a wrong break there
///     reads as a stray list item; a missing break destroys the whole block's rendering).
///     Presentation-only: never applied to text persisted in a conversation.
/// </summary>
public sealed class StreamedTextNormalizer
{
  private readonly StringBuilder _text = new();

  // Line breaks seen since the last non-break character; resolved against the character
  // that ends the run, because a break's meaning depends on both neighbors. Only used
  // in prose mode — breaks inside a fence are emitted verbatim on arrival.
  private int _pendingBreaks;

  // Fence state: master flag for verbatim-break mode, the marker character and the
  // opening run length (a closing fence must repeat the same marker, at least as long).
  private bool _inFence;
  private bool _closingPending;
  private char _fenceChar;
  private int _fenceMarkerLength;

  // Per-line scanner state: while a line could still open or close a fence (only spaces
  // and fence markers seen so far), each character feeds the classifier.
  private int _lineColumn;
  private int _markerRun;
  private char _markerChar;
  private bool _lineScanned;

  // Hold state: a few buffered characters whose meaning (fence opener vs. literal text,
  // list marker vs. a decimal like "3.14") is decided by what follows. While a hold is
  // open, arriving characters buffer instead of appending; the hold commits either as a
  // forced break plus the held characters (the marker case) or as the pre-hold behavior
  // plus the held characters verbatim (the literal case).
  private enum HoldKind
  {
    None,
    FenceMarker,
    ListMarker,
  }

  private HoldKind _hold;
  private readonly List<char> _held = [];
  private bool _holdBreakPending;

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
        if (_hold != HoldKind.None)
        {
          CommitHold(force: false, next: '\n');
        }

        AppendBreak();
        continue;
      }

      if (_pendingBreaks > 0)
      {
        ResolvePending(ch);
        continue;
      }

      if (_hold != HoldKind.None)
      {
        ContinueHold(ch);
        continue;
      }

      if (StartsHold(ch))
      {
        OpenHold(ch);
        continue;
      }

      if (!_lineScanned)
      {
        ScanFenceChar(ch);
      }

      _ = _text.Append(ch);
      _lineColumn++;
    }
  }

  /// <summary>The normalized text so far, with trailing line breaks trimmed.</summary>
  public string Text => _text.ToString().TrimEnd('\n');

  /// <summary>Whether every character of the current line — the last _lineColumn
  ///     characters of the buffer; a pending break is not yet in it — is a space.</summary>
  private bool OnlySpacesSinceLineStart()
  {
    for (int i = Math.Max(0, _text.Length - _lineColumn); i < _text.Length; i++)
    {
      if (_text[i] != ' ')
      {
        return false;
      }
    }

    return true;
  }

  /// <summary>Whether this character opens a hold: a fence marker mid-line (the model
  ///     glued a fence opener to the end of a prose line) or a short digit run after a
  ///     space (a possible list marker). Inside a fence everything is verbatim; at a true
  ///     line start the fence scanner already owns the decision.</summary>
  private bool StartsHold(char ch)
  {
    if (_inFence)
    {
      return false;
    }

    if (!_lineScanned)
    {
      return false; // the line is still a fence candidate: the scanner owns the decision
    }

    if (ch is '`' or '~')
    {
      // A marker whose whole current line (the last _lineColumn characters — a pending
      // break is not yet in the buffer) is spaces sits on an indented fence line the
      // scanner already refused; never hold. Otherwise it is a mid-line run: opener or
      // literal, decided by what follows.
      return !OnlySpacesSinceLineStart();
    }

    if (!char.IsDigit(ch) || _lineColumn == 0 || _text[^1] != ' ')
    {
      return false;
    }

    // A digit run whose whole current line is spaces is an indented code line, never
    // a glued list marker.
    return !OnlySpacesSinceLineStart();
  }

  private void OpenHold(char ch)
  {
    _hold = ch is '`' or '~' ? HoldKind.FenceMarker : HoldKind.ListMarker;
    _held.Clear();
    _held.Add(ch);
    _holdBreakPending = false;
  }

  /// <summary>Resolves the pending break against the next line's first character.
  ///     Paragraph collapses commit immediately; a fence marker or digit holds (the
  ///     marker may be an opener the break must precede); everything else follows the
  ///     classic join rules.</summary>
  private void ResolvePending(char next)
  {
    if (_text.Length == 0)
    {
      _pendingBreaks = 0; // leading breaks are dropped
      FeedNormal(next);
      return;
    }

    if (_pendingBreaks >= 2)
    {
      _ = _text.Append("\n\n");
      _pendingBreaks = 0;
      FeedNormal(next);
      return;
    }

    char prev = _text[^1];
    if (prev is '.' or '!' or '?' or '\u2026')
    {
      // Sentence-ending punctuation keeps its break; the character then starts the
      // new line through the normal path (the fence scanner opens a real fence).
      _pendingBreaks = 0;
      _ = _text.Append('\n');
      ResetLineState();
      FeedNormal(next);
      return;
    }

    if (next is '-' or '*' or '\u2022' or '>')
    {
      // Bullet and quote markers always start a line (existing rule).
      _pendingBreaks = 0;
      _ = _text.Append('\n');
      ResetLineState();
      FeedNormal(next);
      return;
    }

    if (next is '`' or '~' || char.IsDigit(next))
    {
      // The marker may be a fence opener or list marker the model glued to this
      // line's end; hold until the run decides, then force the break or fall back.
      _hold = next is '`' or '~' ? HoldKind.FenceMarker : HoldKind.ListMarker;
      _held.Clear();
      _held.Add(next);
      _holdBreakPending = true;
      _pendingBreaks = 0;
      return;
    }

    EmitBreak(1, next);
    _pendingBreaks = 0;
    FeedNormal(next);
  }

  private void ContinueHold(char ch)
  {
    switch (_hold)
    {
      case HoldKind.None:
        return; // no hold open — unreachable through ContinueHold
      case HoldKind.FenceMarker when ch == _held[0]:
        _held.Add(ch);
        return; // marker run grows; decide at run end
      case HoldKind.ListMarker when char.IsDigit(ch) && _held.Count < 2:
        _held.Add(ch);
        return; // short digit run (1-2 digits; years like 2024 never hold past two)
      case HoldKind.ListMarker when ch == '.' && char.IsDigit(_held[^1]):
        _held.Add(ch);
        return; // dot after the run: still a candidate
      case HoldKind.FenceMarker:
      case HoldKind.ListMarker:
        CommitHold(force: true, next: ch);
        return;
      default:
        CommitHold(force: true, next: ch);
        return;
    }
  }

  /// <summary>Commits the hold. Forced: the held characters proved to be a fence opener
  ///     (marker run of three or more) or a list marker (digits, dot, space) — a break
  ///     precedes them and the line state resets so the fence scanner opens the fence.
  ///     Not forced (a literal marker or decimal, or a hold ended by a line break): the
  ///     pre-hold behavior applies — the pending break, if any, resolves by the classic
  ///     join rules against the first held character — then the held characters append
  ///     verbatim and the incoming character (if any) processes normally.</summary>
  private void CommitHold(bool force, char next)
  {
    HoldKind kind = _hold;
    bool opener = kind == HoldKind.FenceMarker && _held.Count >= 3;
    bool marker = kind == HoldKind.ListMarker && _held.Count >= 2
        && !char.IsDigit(_held[^1]) && next == ' ';
    if (force && (opener || marker))
    {
      // The separator space before a glued opener belongs to the old line's tail:
      // the forced break replaces it.
      while (_text.Length > 0 && _text[^1] == ' ')
      {
        _text.Length -= 1;
      }

      _ = _text.Append('\n');
      ResetLineState();
      foreach (char held in _held)
      {
        FeedNormal(held);
      }
    }
    else
    {
      if (_holdBreakPending)
      {
        EmitBreak(1, _held[0]);
      }

      foreach (char held in _held)
      {
        _ = _text.Append(held);
        _lineColumn++;
      }
      _lineScanned = true; // joined mid-line content can never open a fence
    }

    _hold = HoldKind.None;
    _held.Clear();
    _holdBreakPending = false;

    if (next != '\n')
    {
      FeedNormal(next);
    }
    // next == '\n': the caller (the Append break branch) issues the break itself.
  }

  /// <summary>Feeds one character through the normal (non-held) path: fence scan when
  ///     the line is still a candidate, then append.</summary>
  private void FeedNormal(char ch)
  {
    if (!_lineScanned)
    {
      ScanFenceChar(ch);
    }

    _ = _text.Append(ch);
    _lineColumn++;
  }

  private void ResetLineState()
  {
    _lineColumn = 0;
    _markerRun = 0;
    _lineScanned = false;
  }

  private void AppendBreak()
  {
    if (_inFence)
    {
      _ = _text.Append('\n');
      if (_closingPending)
      {
        // The break ending the closing-fence line: verbatim, and the fence is closed —
        // prose rules resume with the next line's characters.
        _inFence = false;
        _closingPending = false;
      }
    }
    else
    {
      _pendingBreaks++;
    }

    ResetLineState();
  }

  /// <summary>Classifies the character for fence state without altering what is emitted.
  ///     A line is a fence line only while it consists of at most three leading spaces
  ///     followed by a run of fence markers; once opened, the rest of the line is the info
  ///     string, and once a closing run is seen, only whitespace may follow.</summary>
  private void ScanFenceChar(char ch)
  {
    if (ch is '`' or '~')
    {
      if (_markerRun == 0)
      {
        if (_inFence && ch != _fenceChar)
        {
          _lineScanned = true; // a different marker can never close the open fence
          return;
        }

        _markerChar = ch;
        _markerRun = 1;
        return;
      }

      if (ch != _markerChar)
      {
        _lineScanned = true;
        return;
      }

      _markerRun++;
      if (!_inFence && _markerRun >= 3)
      {
        _inFence = true;
        _fenceChar = ch;
        _fenceMarkerLength = _markerRun;
        _lineScanned = true;
      }
      else if (_inFence && _markerRun >= _fenceMarkerLength)
      {
        _closingPending = true;
      }

      return;
    }

    if (ch == ' ' && _markerRun == 0)
    {
      if (_lineColumn >= 3)
      {
        _lineScanned = true; // four-space indent can never be a fence line
      }

      return; // leading space: the line stays a fence candidate
    }

    if (_closingPending && ch is not (' ' or '\t'))
    {
      _closingPending = false; // an info string on a closing line makes it content
    }

    _lineScanned = true;
  }

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
