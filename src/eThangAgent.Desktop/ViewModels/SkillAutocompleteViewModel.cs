using CommunityToolkit.Mvvm.ComponentModel;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>One selectable row of the skill autocomplete popup (plan #30):
///     the catalog's presentation of a skill for invocation.</summary>
internal sealed record SkillOption(string Name, string Description, bool IsManual);

/// <summary>The '/' autocomplete popup state machine (spec #28 task 4): opens
///     ONLY when the input's RAW first character is '/' (no trim), lists the
///     catalog manual-first then name ordinal, filters case-insensitively on
///     the name token alone (text after the first space is the arguments and
///     never affects the filter), navigates with wrap-around, and accepts the
///     full name. Pure state — the view routes keys and applies the autofill;
///     nothing here touches the conversation.</summary>
internal sealed class SkillAutocompleteViewModel(Func<IReadOnlyList<SkillOption>> catalogSource) : ObservableObject
{
  private readonly Func<IReadOnlyList<SkillOption>> _catalogSource = catalogSource ?? throw new ArgumentNullException(nameof(catalogSource));
  private IReadOnlyList<SkillOption> _all = [];

  /// <summary>Whether the popup shows. True only while the raw input starts
  ///     with '/'; plain or leading-whitespace input closes it.</summary>
  public bool IsOpen { get; private set; }

  /// <summary>The filtered options: all skills when the query is empty,
  ///     manual first (IsManual descending) then Name ordinal.</summary>
  public IReadOnlyList<SkillOption> Options { get; private set; } = [];

  /// <summary>The highlighted row (index into <see cref="Options"/>).</summary>
  public int SelectedIndex { get; private set; }

  /// <summary>Feeds the input box's current raw text. The ONLY entry point:
  ///     open/close and the query re-derive from it on every keystroke.</summary>
  public void Update(string input)
  {
    ArgumentNullException.ThrowIfNull(input);
    if (!input.StartsWith('/'))
    {
      Close();
      return;
    }

    // Load (and sort) the catalog once per open; the query filters it.
    if (_all.Count == 0)
    {
      _all = [.. (_catalogSource() ?? throw new InvalidOperationException("The catalog source returned null."))
          .OrderByDescending(o => o.IsManual)
          .ThenBy(o => o.Name, StringComparer.Ordinal)];
    }

    // The filter reads only the token after '/' up to the first space —
    // the remainder is the arguments part and is ignored.
    string token = input[1..];
    int space = token.IndexOf(' ', StringComparison.Ordinal);
    if (space >= 0)
    {
      token = token[..space];
    }

    Options = token.Length == 0 ? _all : [.. _all.Where(o => o.Name.Contains(token, StringComparison.OrdinalIgnoreCase))];
    SelectedIndex = Options.Count > 0 ? 0 : -1;
    IsOpen = true;
    OnPropertyChanged(nameof(IsOpen));
    OnPropertyChanged(nameof(Options));
    OnPropertyChanged(nameof(SelectedIndex));
  }

  /// <summary>Moves the highlight down, wrapping past the end to the first row.</summary>
  public void MoveDown()
  {
    if (Options.Count == 0)
    {
      return;
    }

    SelectedIndex = (SelectedIndex + 1) % Options.Count;
    OnPropertyChanged(nameof(SelectedIndex));
  }

  /// <summary>Moves the highlight up, wrapping past the start to the last row.</summary>
  public void MoveUp()
  {
    if (Options.Count == 0)
    {
      return;
    }

    SelectedIndex = (SelectedIndex - 1 + Options.Count) % Options.Count;
    OnPropertyChanged(nameof(SelectedIndex));
  }

  /// <summary>Accepts the highlighted option: returns the full skill name and
  ///     closes. Null when closed or nothing is selectable.</summary>
  public string? Accept()
  {
    if (!IsOpen || SelectedIndex < 0 || SelectedIndex >= Options.Count)
    {
      Close();
      return null;
    }

    return Accept(Options[SelectedIndex]);
  }

  /// <summary>Accepts a clicked option: same contract as the highlighted
  ///     <c>Accept</c> for the given row (the view maps its item template's
  ///     tap to this).</summary>
  public string? Click(int index) =>
      !IsOpen || index < 0 || index >= Options.Count ? null : Accept(Options[index]);

  /// <summary>Closes without accepting.</summary>
  public void Close()
  {
    IsOpen = false;
    Options = [];
    _all = [];
    SelectedIndex = -1;
    OnPropertyChanged(nameof(IsOpen));
    OnPropertyChanged(nameof(Options));
    OnPropertyChanged(nameof(SelectedIndex));
  }

  private string Accept(SkillOption option)
  {
    Close();
    return option.Name;
  }
}
