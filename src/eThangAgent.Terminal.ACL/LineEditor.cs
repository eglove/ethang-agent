namespace eThangAgent.Terminal.ACL;

/// <summary>
///     Blocking single-line editor: printable input, cursor movement, history recall,
///     Tab completion, and as-you-type ghost suggestions. Reads keys one at a time —
///     event-driven, no polling.
/// </summary>
/// <remarks>
///     The editor owns exactly one console row: its line scrolls horizontally within that row
///     instead of wrapping. Wrapping would push the cursor onto the statusline or past the
///     bottom of the console buffer, and <see cref="System.Console.SetCursorPosition"/> throws
///     <see cref="ArgumentOutOfRangeException"/> for any cell outside the buffer.
/// </remarks>
public sealed class LineEditor(IKeyReader reader, ITextWriter writer)
{
    private readonly List<char> _buffer = new();
    private string _prompt = string.Empty;
    private IList<string>? _history;
    private IAutoCompleter? _completer;
    private int _cursor;
    private int _startLeft;
    private int _startTop;
    private int _scroll;
    private int _historyIndex;
    private string _draft = string.Empty;

    /// <summary>Columns available on the editor's single row, from where the prompt begins to
    ///     the right edge of the console buffer. Never less than one: a width that has already
    ///     shrunk past <see cref="_startLeft"/> still leaves a single writable cell, and the
    ///     cursor is clamped to it in <see cref="SetCursorToColumn"/>.</summary>
    private int ViewWidth => Math.Max(1, writer.BufferWidth - _startLeft);

    public string? Read(string prompt, IList<string>? history = null, IAutoCompleter? completer = null)
    {
        _prompt = prompt;
        _history = history;
        _completer = completer;
        _buffer.Clear();
        _cursor = 0;
        _scroll = 0;
        _draft = string.Empty;
        _historyIndex = history?.Count ?? 0;

        _startLeft = writer.CursorLeft;
        _startTop = writer.CursorTop;
        writer.Write(prompt);
        Render();

        while (true)
        {
            var key = reader.ReadKey();
            if (key is null)
                return null; // EOF

            switch (key.Value.Key)
            {
                case ConsoleKey.Enter:
                    // Leave the cursor at the end of the visible input so the newline lands
                    // after the text rather than mid-line.
                    SetCursorToColumn(Math.Clamp(_prompt.Length + _buffer.Count - _scroll, 0, ViewWidth - 1));
                    writer.WriteLine(string.Empty);
                    var line = new string(_buffer.ToArray());
                    if (history is not null && line.Length > 0)
                        history.Add(line);
                    return line;

                case ConsoleKey.C when key.Value.Modifiers.HasFlag(ConsoleModifiers.Control):
                    _buffer.Clear();
                    _cursor = 0;
                    writer.WriteLine(string.Empty);
                    return string.Empty;

                case ConsoleKey.D when key.Value.Modifiers.HasFlag(ConsoleModifiers.Control) && _buffer.Count == 0:
                    writer.WriteLine(string.Empty);
                    return null;

                case ConsoleKey.Backspace:
                    if (_cursor > 0)
                    {
                        _buffer.RemoveAt(_cursor - 1);
                        _cursor--;
                        Render();
                    }
                    break;

                case ConsoleKey.Delete:
                    if (_cursor < _buffer.Count)
                    {
                        _buffer.RemoveAt(_cursor);
                        Render();
                    }
                    break;

                case ConsoleKey.LeftArrow:
                    if (_cursor > 0)
                    {
                        _cursor--;
                        Render();
                    }
                    break;

                case ConsoleKey.RightArrow:
                    if (_cursor < _buffer.Count)
                    {
                        _cursor++;
                        Render();
                    }
                    break;

                case ConsoleKey.Home:
                    _cursor = 0;
                    Render();
                    break;

                case ConsoleKey.End:
                    _cursor = _buffer.Count;
                    Render();
                    break;

                case ConsoleKey.UpArrow:
                    if (_history is { } up && _historyIndex > 0)
                    {
                        if (_historyIndex == up.Count)
                            _draft = new string(_buffer.ToArray());
                        _historyIndex--;
                        ReplaceBuffer(up[_historyIndex]);
                    }
                    break;

                case ConsoleKey.DownArrow:
                    if (_history is { } down && _historyIndex < down.Count)
                    {
                        _historyIndex++;
                        ReplaceBuffer(_historyIndex == down.Count ? _draft : down[_historyIndex]);
                    }
                    break;

                case ConsoleKey.Tab:
                    if (_completer is not null && _cursor == _buffer.Count)
                    {
                        var suggestion = _completer.Suggest(new string(_buffer.ToArray()));
                        if (suggestion is not null)
                            ReplaceBuffer(suggestion);
                    }
                    break;

                default:
                    var c = key.Value.KeyChar;
                    if (!char.IsControl(c))
                    {
                        _buffer.Insert(_cursor, c);
                        _cursor++;
                        Render();
                    }
                    break;
            }
        }
    }

    private void ReplaceBuffer(string text)
    {
        _buffer.Clear();
        _buffer.AddRange(text);
        _cursor = _buffer.Count;
        Render();
    }

    private void Render()
    {
        var typed = _prompt + new string(_buffer.ToArray());
        var ghost = string.Empty;
        if (_cursor == _buffer.Count && _completer is not null)
        {
            var suggestion = _completer.Suggest(new string(_buffer.ToArray()));
            if (suggestion is not null && suggestion.Length > _buffer.Count)
                ghost = suggestion[_buffer.Count..];
        }

        var cursorColumn = _prompt.Length + _cursor;
        var fullLength = typed.Length + ghost.Length;

        // Single-row horizontal scroll: keep the cursor visible and anchor the line to the
        // left whenever the whole line fits. The editor never advances to a second row.
        if (fullLength <= ViewWidth)
            _scroll = 0;
        else if (cursorColumn < _scroll)
            _scroll = cursorColumn;
        else if (cursorColumn >= _scroll + ViewWidth)
            _scroll = cursorColumn - ViewWidth + 1;

        var visibleTyped = Slice(typed, _scroll, ViewWidth);
        var visibleGhost = Slice(ghost, Math.Max(0, _scroll - typed.Length),
            Math.Max(0, ViewWidth - visibleTyped.Length));

        SetCursorToColumn(0);
        writer.Write(visibleTyped);
        if (visibleGhost.Length > 0)
            writer.Write(visibleGhost, ConsoleColor.DarkGray);

        // Pad the rest of the row so a previously longer render leaves no stale text.
        var rendered = visibleTyped.Length + visibleGhost.Length;
        if (rendered < ViewWidth)
            writer.Write(new string(' ', ViewWidth - rendered));

        SetCursorToColumn(cursorColumn - _scroll);
    }

    /// <summary>Positions the cursor within the editor's single row. The top coordinate never
    ///     changes and the left coordinate is clamped to the buffer width, so this method can
    ///     never target a cell outside the console buffer. The clamp is a deliberate, documented
    ///     decision: an editor that owns one row never wraps into rows it does not own.</summary>
    private void SetCursorToColumn(int column)
    {
        var left = Math.Clamp(_startLeft + column, 0, Math.Max(0, writer.BufferWidth - 1));
        writer.SetCursorPosition(left, _startTop);
    }

    private static string Slice(string value, int start, int length)
    {
        if (length <= 0 || start >= value.Length)
            return string.Empty;
        return value[start..Math.Min(value.Length, start + length)];
    }
}
