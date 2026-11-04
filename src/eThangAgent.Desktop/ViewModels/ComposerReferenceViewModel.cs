using CommunityToolkit.Mvvm.ComponentModel;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>What a reference option resolves against.</summary>
internal enum ReferenceKind
{
  File,
  Skill,
  Command,
}

/// <summary>One row of the reference popup: <paramref name="Name"/> is what the
///     query matched, <paramref name="Detail"/> is the secondary line, and
///     <paramref name="InsertText"/> is the text the commit writes over the token.</summary>
internal sealed record ReferenceOption(string Name, string Detail, ReferenceKind Kind, string InsertText);

/// <summary>One accepted reference: replace <paramref name="Length"/> characters at
///     <paramref name="Start"/> with <paramref name="InsertText"/>, then place the caret
///     at <paramref name="CaretIndex"/>.</summary>
internal sealed record ReferenceCommit(int Start, int Length, string InsertText, int CaretIndex);

/// <summary>The reference popup state machine: opens only while the caret sits inside an
///     active token, lists the search's options sorted by kind then name, navigates with
///     wrap-around, and commits a replacement span. The search delegate is synchronous and
///     reads an already-cached catalog. Pure state — the view routes keys and applies the
///     commit; nothing here touches the conversation.</summary>
internal sealed class ComposerReferenceViewModel(Func<string, IReadOnlyList<ReferenceOption>> search) : ObservableObject
{
  private readonly Func<string, IReadOnlyList<ReferenceOption>> _search = search ?? throw new ArgumentNullException(nameof(search));
  private ReferenceToken? _token;
  private int _committedStart = -1;
  private int _committedEnd = -1;
  private string? _committedText;

  /// <summary>Whether the popup shows.</summary>
  public bool IsOpen { get; private set; }

  /// <summary>The options for the active token, skills before commands before files, then name ordinal.</summary>
  public IReadOnlyList<ReferenceOption> Options { get; private set; } = [];

  /// <summary>The highlighted row (index into <see cref="Options"/>), or -1 when nothing is selectable.</summary>
  public int SelectedIndex { get; private set; } = -1;

  /// <summary>Feeds the composer's text and caret. The ONLY entry point: open, close, and
  ///     the option list re-derive from them on every keystroke.</summary>
  public void Update(string input, int caretIndex)
  {
    ArgumentNullException.ThrowIfNull(input);
    if (_committedText is not null && !CommittedSpanIntact(input))
    {
      ClearCommit();
    }

    ReferenceToken? token = ReferenceTokenParser.FindActive(input, caretIndex);
    if (token is null)
    {
      Close();
      return;
    }

    if (token.Start == _committedStart && caretIndex <= _committedEnd)
    {
      Close();
      return;
    }

    ClearCommit();
    IReadOnlyList<ReferenceOption> matches = _search(token.Query)
        ?? throw new InvalidOperationException("The reference search returned null.");
    Options =
    [
      .. matches.OrderBy(o => KindRank(o.Kind)).ThenBy(o => o.Name, StringComparer.Ordinal),
    ];
    // No match closes the popup rather than showing an empty box (spec 6).
    if (Options.Count == 0)
    {
      Close();
      return;
    }

    _token = token;
    SelectedIndex = 0;
    IsOpen = true;
    RaiseAll();
  }

  /// <summary>Moves the highlight down, wrapping past the end to the first row.</summary>
  public void MoveDown()
  {
    if (!IsOpen || Options.Count == 0)
    {
      return;
    }

    SelectedIndex = (SelectedIndex + 1) % Options.Count;
    OnPropertyChanged(nameof(SelectedIndex));
  }

  /// <summary>Moves the highlight up, wrapping past the start to the last row.</summary>
  public void MoveUp()
  {
    if (!IsOpen || Options.Count == 0)
    {
      return;
    }

    SelectedIndex = (SelectedIndex - 1 + Options.Count) % Options.Count;
    OnPropertyChanged(nameof(SelectedIndex));
  }

  /// <summary>Accepts the highlighted option. Null when the popup is closed or nothing is selectable.</summary>
  public ReferenceCommit? Accept()
  {
    if (!IsOpen || SelectedIndex < 0 || SelectedIndex >= Options.Count)
    {
      Close();
      return null;
    }

    return Commit(Options[SelectedIndex]);
  }

  /// <summary>Accepts a clicked option: same contract as the highlighted <see cref="Accept"/>.
  ///     Null when the popup is closed or the index is out of range.</summary>
  public ReferenceCommit? Click(int index) =>
      !IsOpen || index < 0 || index >= Options.Count ? null : Commit(Options[index]);

  /// <summary>Closes without accepting; the committed span, if any, stays remembered.</summary>
  public void Close()
  {
    bool changed = IsOpen || Options.Count > 0 || SelectedIndex != -1;
    _token = null;
    IsOpen = false;
    Options = [];
    SelectedIndex = -1;
    if (changed)
    {
      RaiseAll();
    }
  }

  private ReferenceCommit Commit(ReferenceOption option)
  {
    ReferenceToken token = _token ?? throw new InvalidOperationException("No reference token is active.");
    string insertText = option.InsertText + " ";
    int caretIndex = token.Start + insertText.Length;
    _committedStart = token.Start;
    _committedEnd = caretIndex;
    _committedText = insertText;
    Close();
    return new ReferenceCommit(token.Start, token.Length, insertText, caretIndex);
  }

  private static int KindRank(ReferenceKind kind) => kind switch
  {
    ReferenceKind.Skill => 0,
    ReferenceKind.Command => 1,
    ReferenceKind.File => 2,
    _ => 3,
  };

  private bool CommittedSpanIntact(string input) =>
      _committedText is not null
      && input.Length >= _committedEnd
      && string.CompareOrdinal(input, _committedStart, _committedText, 0, _committedText.Length) == 0;

  private void ClearCommit()
  {
    _committedStart = -1;
    _committedEnd = -1;
    _committedText = null;
  }

  private void RaiseAll()
  {
    OnPropertyChanged(nameof(IsOpen));
    OnPropertyChanged(nameof(Options));
    OnPropertyChanged(nameof(SelectedIndex));
  }
}
