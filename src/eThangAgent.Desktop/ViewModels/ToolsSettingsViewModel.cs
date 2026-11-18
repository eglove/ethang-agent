using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eThangAgent.Composition;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>One listed tool: its bare name, the PATH directory it came from (null for a name
///     added by hand), whether this machine can run it now, and whether it is ticked.
///     <para>A row that does not resolve stays ticked and stays stored; the page says so and
///     the bootstrap leaves the name out.</para></summary>
internal sealed partial class ToolRowViewModel(
    ToolsSettingsViewModel owner, string name, string? sourceDirectory, bool resolves, bool ticked)
    : ObservableObject
{
  private readonly ToolsSettingsViewModel _owner =
      owner ?? throw new ArgumentNullException(nameof(owner));

  /// <summary>The bare name, which is what the preference stores and the prompt announces.</summary>
  public string Name { get; } = name;

  /// <summary>The PATH directory the file was found in, or null for a name added by hand.</summary>
  public string? SourceDirectory { get; } = sourceDirectory;

  /// <summary>Whether this machine would run the name right now.</summary>
  public bool Resolves { get; } = resolves;

  /// <summary>True when the name is ticked but nothing on the PATH would run it.</summary>
  public bool IsUnavailable => !Resolves;

  /// <summary>What the row says about runnability, in the user's terms.</summary>
  public string AvailabilityText => Resolves ? "available" : "unavailable - not on the PATH";

  /// <summary>Whether the user ticked this tool.</summary>
  [ObservableProperty]
  public partial bool IsTicked { get; set; } = ticked;

  partial void OnIsTickedChanged(bool value) => _owner.NoteTicked(this);
}

/// <summary>One PATH directory's tools, plus the heading the page shows for it. A group whose
///     directory is null is the names that are ticked but were not found on the PATH.</summary>
internal sealed partial class ToolGroupViewModel : ObservableObject
{
  /// <summary>The heading the page gives the group that holds names added by hand.</summary>
  internal const string HandAddedHeader = "Added by hand - not on the PATH";

  private readonly ToolsSettingsViewModel _owner;

  public ToolGroupViewModel(ToolsSettingsViewModel owner, string? directory,
      IReadOnlyList<ToolRowViewModel> rows)
  {
    _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    ArgumentNullException.ThrowIfNull(rows);
    Directory = directory;
    Rows = rows;
    IsExpanded = owner.WasExpanded(directory);
  }

  /// <summary>The PATH directory this group came from, or null for the hand-added group.</summary>
  public string? Directory { get; }

  /// <summary>The rows this group shows, in name order.</summary>
  public IReadOnlyList<ToolRowViewModel> Rows { get; }

  /// <summary>How many tools the group holds.</summary>
  public int ToolCount => Rows.Count;

  /// <summary>The heading: the directory path and the count. Groups open collapsed, because
  ///     one PATH directory can hold several hundred tools.</summary>
  public string HeaderText =>
      (Directory ?? HandAddedHeader) + "  (" + ToolCount.ToString(CultureInfo.InvariantCulture) + " tools)";

  /// <summary>Whether the group is open. Collapsing one is a view state the page keeps across
  ///     a search, so typing does not close the group the user just opened.</summary>
  [ObservableProperty]
  public partial bool IsExpanded { get; set; }

  partial void OnIsExpandedChanged(bool value) => _owner.NoteExpanded(Directory, value);
}

/// <summary>View-model behind the Tools page: every console program and script on this
///     machine's PATH, grouped by the directory it lives in, with a search box, a ticked
///     count, a free-text add box, and Save.
///     <para>THE SCAN BELONGS TO THIS PAGE, not to app launch: reading every PE header costs
///     several seconds, and only a user who opens the page is waiting for it.</para>
///     <para>NOTHING IS TICKED BY DEFAULT and no name is seeded: a fresh install announces no
///     tools at all, and the list is a guess about nobody's machine.</para>
///     <para>The scan and the resolution arrive as seams so the page is testable without
///     touching the machine's PATH.</para></summary>
internal sealed partial class ToolsSettingsViewModel : ObservableObject
{
  /// <summary>What the page says after a save landed.</summary>
  internal const string SavedMessage =
      "Saved. Agents opened from now on are told about these tools; a change applies from the next app launch.";

  /// <summary>What the page says after an empty selection was saved: the preference is gone,
  ///     so the next agent's prompt is byte-identical to one built without this feature.</summary>
  internal const string NothingTickedMessage =
      "Saved. Nothing is ticked, so agents are told about no tools. Applies from the next app launch.";

  private readonly IAppPreferenceStore? _preferences;
  private readonly Func<PathToolScan> _scan;
  private readonly Func<IReadOnlyList<string>, IReadOnlyList<PathToolResolution>> _resolve;

  /// <summary>The directories the user has opened, so a search does not collapse them.</summary>
  private readonly HashSet<string> _expanded = [with(StringComparer.OrdinalIgnoreCase)];

  /// <summary>Every row the page holds, filtered or not. The selection is saved from THIS, so
  ///     a search never narrows what gets written.</summary>
  private List<ToolRowViewModel> _allRows = [];

  public ToolsSettingsViewModel(
      IAppPreferenceStore? preferences,
      Func<PathToolScan>? scan = null,
      Func<IReadOnlyList<string>, IReadOnlyList<PathToolResolution>>? resolve = null)
  {
    _preferences = preferences;
    _scan = scan ?? (() => PathToolDiscovery.Scan(ResolvedAvailableTools.MachinePath()));
    _resolve = resolve ?? (names => PathToolDiscovery.Resolve(names, ResolvedAvailableTools.MachinePath()));
    LoadCommand = new AsyncRelayCommand(LoadAsync);
    SaveCommand = new AsyncRelayCommand(SaveAsync);
    AddNameCommand = new RelayCommand(AddName, () => NameToAdd.Trim().Length > 0);
  }

  /// <summary>The groups the page shows: one per source directory, in PATH order, plus the
  ///     hand-added group last. Rebuilt by every search, so a group with no match is absent
  ///     rather than present and empty.</summary>
  public ObservableCollection<ToolGroupViewModel> Groups { get; } = [];

  /// <summary>Scans the PATH and rebuilds the page. The view calls this when the page opens.</summary>
  public IAsyncRelayCommand LoadCommand { get; }

  /// <summary>Writes the ticked names to the preference, or deletes it when nothing is ticked.</summary>
  public IAsyncRelayCommand SaveCommand { get; }

  /// <summary>Ticks the name in the add box, creating a row for it when the PATH holds none.</summary>
  public IRelayCommand AddNameCommand { get; }

  /// <summary>The name filter. Matched case-insensitively against the tool name only.</summary>
  [ObservableProperty]
  public partial string SearchText { get; set; } = string.Empty;

  /// <summary>The name typed into the add box.</summary>
  [ObservableProperty]
  public partial string NameToAdd { get; set; } = string.Empty;

  /// <summary>The line under the page: what the scan found, or what the last save did.</summary>
  [ObservableProperty]
  public partial string? StatusText { get; set; }

  /// <summary>True while the PATH is being read, which takes seconds.</summary>
  [ObservableProperty]
  public partial bool IsLoading { get; set; }

  /// <summary>How many rows are ticked, filtered or not.</summary>
  public int TickedCount => _allRows.Count(row => row.IsTicked);

  /// <summary>How many tools the page lists, filtered or not.</summary>
  public int TotalCount => _allRows.Count;

  /// <summary>The counts in one line.</summary>
  public string SummaryText =>
      TickedCount.ToString(CultureInfo.InvariantCulture) + " of "
      + TotalCount.ToString(CultureInfo.InvariantCulture) + " tools ticked";

  /// <summary>Reads the PATH and the stored selection and rebuilds the page. Runs once per
  ///     open: the scan is the expensive part, and a tool installed mid-run needs a restart
  ///     anyway.</summary>
  public async Task LoadAsync()
  {
    if (IsLoading)
    {
      return;
    }

    IsLoading = true;
    StatusText = "Reading the PATH...";
    try
    {
      PathToolScan scan = await Task.Run(_scan).ConfigureAwait(true);
      IReadOnlyList<string> ticked = await ReadTickedAsync().ConfigureAwait(true);
      _allRows = BuildRows(scan, ticked);
      ApplyFilter();
      RaiseCounts();
      StatusText = scan.MissingDirectoryCount == 0
          ? null
          : scan.MissingDirectoryCount.ToString(CultureInfo.InvariantCulture)
              + " PATH directories could not be read and contribute nothing.";
    }
    finally
    {
      IsLoading = false;
    }
  }

  /// <summary>Writes the ticked names through the preference's own serializer, or deletes the
  ///     preference when nothing is ticked: the store requires a non-blank value, so an empty
  ///     selection is expressed as a delete and never as a written blank.</summary>
  public async Task SaveAsync()
  {
    if (_preferences is null)
    {
      StatusText = "This host keeps no preferences, so the selection cannot be saved.";
      return;
    }

    List<string> names = [.. _allRows.Where(row => row.IsTicked).Select(row => row.Name)];
    if (names.Count == 0)
    {
      _ = await _preferences.DeleteAsync(PathToolPreferences.Key).ConfigureAwait(true);
      StatusText = NothingTickedMessage;
      return;
    }

    bool stored = await _preferences
        .SetAsync(PathToolPreferences.Key, PathToolPreferences.Serialize(names))
        .ConfigureAwait(true);
    StatusText = stored ? SavedMessage : "The selection could not be saved.";
  }

  /// <summary>Records a row's tick so the counts follow it.</summary>
  internal void NoteTicked(ToolRowViewModel row)
  {
    ArgumentNullException.ThrowIfNull(row);
    RaiseCounts();
  }

  /// <summary>Records whether a group is open, so the state survives a search.</summary>
  internal void NoteExpanded(string? directory, bool expanded)
  {
    string key = directory ?? string.Empty;
    _ = expanded ? _expanded.Add(key) : _expanded.Remove(key);
  }

  /// <summary>Whether the user had this group open before the last rebuild.</summary>
  internal bool WasExpanded(string? directory) => _expanded.Contains(directory ?? string.Empty);

  /// <summary>The stored selection, or nothing ticked when the value does not parse (the
  ///     failure is stated rather than silently swallowed, and nothing is half-ticked).</summary>
  private async Task<IReadOnlyList<string>> ReadTickedAsync()
  {
    if (_preferences is null)
    {
      return [];
    }

    string? stored = await _preferences.GetAsync(PathToolPreferences.Key).ConfigureAwait(true);
    Result<IReadOnlyList<string>> parsed = PathToolPreferences.Parse(stored);
    if (parsed.IsSuccess)
    {
      return parsed.Value;
    }

    StatusText = parsed.Error.Message;
    return [];
  }

  /// <summary>Every listed tool, grouped by the directory the scan found it in, plus a row for
  ///     every stored name the scan did not find. A stored name is always ticked, whether or
  ///     not it resolves: the page states that it is unavailable and Save keeps it.</summary>
  private List<ToolRowViewModel> BuildRows(PathToolScan scan, IReadOnlyList<string> ticked)
  {
    HashSet<string> listed = [with(StringComparer.OrdinalIgnoreCase)];
    List<ToolRowViewModel> rows = [];
    foreach (PathToolDirectory directory in scan.Directories)
    {
      listed.UnionWith(directory.Tools.Select(tool => tool.Name));
      rows.AddRange(directory.Tools.Select(tool => new ToolRowViewModel(
          this, tool.Name, directory.Directory, resolves: true,
          ticked: ticked.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))));
    }

    string[] absent = [.. ticked.Where(name => !listed.Contains(name))];
    if (absent.Length > 0)
    {
      foreach (PathToolResolution resolution in _resolve(absent))
      {
        rows.Add(new ToolRowViewModel(this, resolution.Name, sourceDirectory: null,
            resolves: resolution.IsResolved, ticked: true));
      }
    }

    return rows;
  }

  /// <summary>Rebuilds the groups from the rows the search keeps. Grouping preserves the
  ///     first-seen order, so the groups follow PATH order and the hand-added group is last.</summary>
  private void ApplyFilter()
  {
    string search = SearchText.Trim();
    IEnumerable<ToolRowViewModel> visible = search.Length == 0
        ? _allRows
        : _allRows.Where(row => row.Name.Contains(search, StringComparison.OrdinalIgnoreCase));

    Groups.Clear();
    foreach (IGrouping<string?, ToolRowViewModel> group in visible.GroupBy(row => row.SourceDirectory))
    {
      Groups.Add(new ToolGroupViewModel(this, group.Key, [.. group]));
    }
  }

  /// <summary>Ticks the typed name, adding a row for it when the PATH holds none. A name that
  ///     is already listed is ticked rather than duplicated, and a name nothing resolves stays
  ///     available to save: the user may install it later.</summary>
  private void AddName()
  {
    if (BareName(NameToAdd) is not { } bare)
    {
      return;
    }

    NameToAdd = string.Empty;
    ToolRowViewModel? existing = _allRows.FirstOrDefault(
        row => string.Equals(row.Name, bare, StringComparison.OrdinalIgnoreCase));
    if (existing is not null)
    {
      existing.IsTicked = true;
      return;
    }

    bool resolves = _resolve([bare]) is [{ IsResolved: true }, ..];
    _allRows.Add(new ToolRowViewModel(this, bare, sourceDirectory: null, resolves, ticked: true));
    ApplyFilter();
    RaiseCounts();
  }

  /// <summary>The bare name a typed entry stands for, or null when the text is not a tool name
  ///     at all: blank, or nothing but an executable extension. The normalization is the
  ///     preference's own, so a name typed with an extension is stored the way a scanned name is.</summary>
  private static string? BareName(string typed)
  {
    string trimmed = typed.Trim();
    if (trimmed.Length == 0)
    {
      return null;
    }

    try
    {
      Result<IReadOnlyList<string>> parsed =
          PathToolPreferences.Parse(PathToolPreferences.Serialize([trimmed]));
      return parsed.IsSuccess && parsed.Value.Count > 0 ? parsed.Value[0] : null;
    }
    catch (ArgumentException)
    {
      return null;
    }
  }

  private void RaiseCounts()
  {
    OnPropertyChanged(nameof(TickedCount));
    OnPropertyChanged(nameof(TotalCount));
    OnPropertyChanged(nameof(SummaryText));
  }

  partial void OnSearchTextChanged(string value) => ApplyFilter();

  partial void OnNameToAddChanged(string value) => AddNameCommand.NotifyCanExecuteChanged();
}
