namespace eThangAgent.Terminal.ACL;

/// <summary>
///     Blocking single-line editor: printable input, cursor movement, history recall,
///     Tab completion, and as-you-type ghost suggestions. Reads keys one at a time —
///     event-driven, no polling.
/// </summary>
public sealed class LineEditor(IKeyReader reader, ITextWriter writer)
{
    private readonly List<char> _buffer = new();
    private string _prompt = string.Empty;
    private IList<string>? _history;
    private IAutoCompleter? _completer;
    private int _cursor;
    private int _startLeft;
    private int _startTop;
    private int _lastRenderedLength;
    private int _historyIndex;
    private string _draft = string.Empty;

    public string? Read(string prompt, IList<string>? history = null, IAutoCompleter? completer = null)
    {
        _prompt = prompt;
        _history = history;
        _completer = completer;
        _buffer.Clear();
        _cursor = 0;
        _lastRenderedLength = 0;
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
                    MoveCursorToCell(_prompt.Length + _buffer.Count);
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
                        MoveCursorToCell(_prompt.Length + _cursor);
                    }
                    break;

                case ConsoleKey.RightArrow:
                    if (_cursor < _buffer.Count)
                    {
                        _cursor++;
                        MoveCursorToCell(_prompt.Length + _cursor);
                    }
                    break;

                case ConsoleKey.Home:
                    _cursor = 0;
                    MoveCursorToCell(_prompt.Length + _cursor);
                    break;

                case ConsoleKey.End:
                    _cursor = _buffer.Count;
                    MoveCursorToCell(_prompt.Length + _cursor);
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

        MoveCursorToCell(0);
        writer.Write(typed);
        var renderedLength = typed.Length;
        if (ghost.Length > 0)
        {
            writer.Write(ghost, ConsoleColor.DarkGray);
            renderedLength += ghost.Length;
        }
        if (_lastRenderedLength > renderedLength)
            writer.Write(new string(' ', _lastRenderedLength - renderedLength));
        _lastRenderedLength = renderedLength;

        MoveCursorToCell(_prompt.Length + _cursor);
    }

    private void MoveCursorToCell(int offset)
    {
        var total = _startLeft + offset;
        writer.SetCursorPosition(total % writer.BufferWidth, _startTop + total / writer.BufferWidth);
    }
}
