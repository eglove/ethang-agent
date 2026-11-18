using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.Tests;

/// <summary>The tools page: the PATH listing grouped by source directory, the name filter, the
///     ticked counts, the free-text add box, and the save that writes the ticked bare names.
///     The scan and the resolution are seams, so nothing here reads this machine's PATH.</summary>
public class ToolsSettingsViewModelTests
{
  private const string FirstDirectory = @"C:\tools\first";
  private const string SecondDirectory = @"C:\tools\second";

  /// <summary>An in-memory preference store that records which call wrote and which deleted,
  ///     so a save can be observed as a delete rather than as a written blank.</summary>
  private sealed class MemoryPreferences : IAppPreferenceStore
  {
    private readonly Dictionary<string, string> _values = [with(StringComparer.Ordinal)];

    public List<string> Writes { get; } = [];

    public List<string> Deletes { get; } = [];

    public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_values.TryGetValue(key, out string? value) ? value : null);

    public Task<bool> SetAsync(string key, string value, CancellationToken ct = default)
    {
      Writes.Add(key);
      _values[key] = value;
      return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
      Deletes.Add(key);
      return Task.FromResult(_values.Remove(key));
    }

    public bool Holds(string key) => _values.ContainsKey(key);
  }

  /// <summary>A discovery result holding the given tools, grouped as the scan groups them.</summary>
  private static PathToolScan ScanOf(params (string Directory, string[] Names)[] directories) =>
      new(
          [.. directories.Select(group => new PathToolDirectory(group.Directory,
              [.. group.Names.Select(name => new PathTool(name, group.Directory,
                  Path.Combine(group.Directory, name + ".exe"), PathToolKind.ConsoleProgram))]))],
          0);

  /// <summary>A resolution seam that resolves the names it was told resolve and reports every
  ///     other name unresolved, so a hand-added or since-uninstalled name is exercised without
  ///     a file system.</summary>
  private static Func<IReadOnlyList<string>, IReadOnlyList<PathToolResolution>> Resolver(
      params string[] resolving) =>
      names =>
      [
          .. names.Select(name => resolving.Contains(name, StringComparer.OrdinalIgnoreCase)
              ? new PathToolResolution(name, PathToolKind.ConsoleProgram,
                  Path.Combine(FirstDirectory, name + ".exe"))
              : new PathToolResolution(name, null, null)),
      ];

  private static ToolsSettingsViewModel Page(MemoryPreferences preferences, PathToolScan scan,
      Func<IReadOnlyList<string>, IReadOnlyList<PathToolResolution>>? resolve = null) =>
      new(preferences, () => scan, resolve ?? Resolver());

  [Fact]
  public async Task ThePageGroupsEveryListedToolUnderItsSourceDirectory()
  {
    MemoryPreferences preferences = new();
    ToolsSettingsViewModel vm = Page(preferences,
        ScanOf((FirstDirectory, ["rg", "jq"]), (SecondDirectory, ["fzf"])));

    await vm.LoadAsync();

    Assert.Equal(2, vm.Groups.Count);
    Assert.Equal([FirstDirectory, SecondDirectory], vm.Groups.Select(group => group.Directory));
    Assert.Equal(["rg", "jq"], vm.Groups[0].Rows.Select(row => row.Name));
    Assert.Equal(["fzf"], vm.Groups[1].Rows.Select(row => row.Name));
    Assert.Equal(3, vm.TotalCount);
  }

  [Fact]
  public async Task EachGroupStatesItsDirectoryAndItsCount_AndOpensCollapsed()
  {
    MemoryPreferences preferences = new();
    ToolsSettingsViewModel vm = Page(preferences, ScanOf((FirstDirectory, ["rg", "jq"])));

    await vm.LoadAsync();

    ToolGroupViewModel group = vm.Groups[0];
    Assert.Equal(2, group.ToolCount);
    Assert.Contains(FirstDirectory, group.HeaderText, StringComparison.Ordinal);
    Assert.Contains("2 tools", group.HeaderText, StringComparison.Ordinal);
    Assert.False(group.IsExpanded);
  }

  [Fact]
  public async Task TheSearchKeepsOnlyMatchingRows_AndHidesGroupsLeftWithNoMatch()
  {
    MemoryPreferences preferences = new();
    ToolsSettingsViewModel vm = Page(preferences,
        ScanOf((FirstDirectory, ["rg", "jq"]), (SecondDirectory, ["fzf"])));
    await vm.LoadAsync();

    vm.SearchText = "RG";

    ToolGroupViewModel group = Assert.Single(vm.Groups);
    Assert.Equal(FirstDirectory, group.Directory);
    Assert.Equal(["rg"], group.Rows.Select(row => row.Name));
    Assert.Equal(3, vm.TotalCount);

    vm.SearchText = string.Empty;
    Assert.Equal(2, vm.Groups.Count);
  }

  [Fact]
  public async Task ThePageShowsATickedCountAndATotalCount()
  {
    MemoryPreferences preferences = new();
    ToolsSettingsViewModel vm = Page(preferences, ScanOf((FirstDirectory, ["rg", "jq"])));
    await vm.LoadAsync();

    Assert.Equal(0, vm.TickedCount);
    Assert.Equal(2, vm.TotalCount);
    Assert.Contains("0 of 2", vm.SummaryText, StringComparison.Ordinal);

    vm.Groups[0].Rows[0].IsTicked = true;

    Assert.Equal(1, vm.TickedCount);
    Assert.Contains("1 of 2", vm.SummaryText, StringComparison.Ordinal);
  }

  [Fact]
  public async Task TickingTwoRowsAndSavingWritesExactlyThoseTwoBareNames()
  {
    MemoryPreferences preferences = new();
    ToolsSettingsViewModel vm = Page(preferences, ScanOf((FirstDirectory, ["rg", "jq", "fzf"])));
    await vm.LoadAsync();

    vm.Groups[0].Rows[0].IsTicked = true;
    vm.Groups[0].Rows[1].IsTicked = true;
    await vm.SaveAsync();

    string? stored = await preferences.GetAsync(PathToolPreferences.Key, TestContext.Current.CancellationToken);
    Assert.NotNull(stored);
    Assert.Equal(["rg", "jq"], PathToolPreferences.Parse(stored).Value);
    Assert.Empty(preferences.Deletes);
  }

  [Fact]
  public async Task SavingAnEmptySelectionDeletesThePreferenceInsteadOfWritingABlank()
  {
    MemoryPreferences preferences = new();
    _ = await preferences.SetAsync(PathToolPreferences.Key, "[\"rg\"]", TestContext.Current.CancellationToken);
    preferences.Writes.Clear();
    ToolsSettingsViewModel vm = Page(preferences, ScanOf((FirstDirectory, ["rg"])));
    await vm.LoadAsync();

    Assert.Equal(1, vm.TickedCount);
    vm.Groups[0].Rows[0].IsTicked = false;
    await vm.SaveAsync();

    Assert.False(preferences.Holds(PathToolPreferences.Key));
    Assert.Equal([PathToolPreferences.Key], preferences.Deletes);
    Assert.Empty(preferences.Writes);
    Assert.Contains("Nothing is ticked", vm.StatusText, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AStoredNameTheScanDidNotFindIsListedTickedAndUnavailable()
  {
    MemoryPreferences preferences = new();
    _ = await preferences.SetAsync(PathToolPreferences.Key, "[\"rg\",\"mytool\"]", TestContext.Current.CancellationToken);
    ToolsSettingsViewModel vm = Page(preferences, ScanOf((FirstDirectory, ["rg"])),
        Resolver("rg"));

    await vm.LoadAsync();

    ToolRowViewModel absent = vm.Groups.SelectMany(group => group.Rows)
        .Single(row => row.Name == "mytool");
    Assert.True(absent.IsTicked);
    Assert.False(absent.Resolves);
    Assert.True(absent.IsUnavailable);
    Assert.Null(absent.SourceDirectory);
    Assert.Equal(2, vm.TickedCount);

    await vm.SaveAsync();
    string? stored = await preferences.GetAsync(PathToolPreferences.Key, TestContext.Current.CancellationToken);
    Assert.Equal(["rg", "mytool"], PathToolPreferences.Parse(stored).Value);
  }

  [Fact]
  public async Task AddingANameByHandTicksItAndSavesIt()
  {
    MemoryPreferences preferences = new();
    ToolsSettingsViewModel vm = Page(preferences, ScanOf((FirstDirectory, ["rg"])), Resolver("rg"));
    await vm.LoadAsync();

    vm.NameToAdd = "mytool";
    vm.AddNameCommand.Execute(null);

    ToolRowViewModel added = vm.Groups.SelectMany(group => group.Rows)
        .Single(row => row.Name == "mytool");
    Assert.True(added.IsTicked);
    Assert.True(added.IsUnavailable);
    Assert.Equal(string.Empty, vm.NameToAdd);

    await vm.SaveAsync();
    string? stored = await preferences.GetAsync(PathToolPreferences.Key, TestContext.Current.CancellationToken);
    Assert.Equal(["mytool"], PathToolPreferences.Parse(stored).Value);
  }

  [Fact]
  public async Task AddingANameThePathAlreadyListsTicksThatRowInsteadOfDuplicatingIt()
  {
    MemoryPreferences preferences = new();
    ToolsSettingsViewModel vm = Page(preferences, ScanOf((FirstDirectory, ["rg"])), Resolver("rg"));
    await vm.LoadAsync();

    vm.NameToAdd = "rg.exe";
    vm.AddNameCommand.Execute(null);

    ToolRowViewModel row = Assert.Single(vm.Groups.SelectMany(group => group.Rows));
    Assert.Equal("rg", row.Name);
    Assert.True(row.IsTicked);
    Assert.Equal(1, vm.TotalCount);
  }

  [Fact]
  public async Task ThePageOpensWithTheStoredSelectionAlreadyTicked()
  {
    MemoryPreferences preferences = new();
    _ = await preferences.SetAsync(PathToolPreferences.Key, "[\"jq\"]", TestContext.Current.CancellationToken);
    ToolsSettingsViewModel vm = Page(preferences, ScanOf((FirstDirectory, ["rg", "jq"])));

    await vm.LoadAsync();

    Assert.Equal(["jq"], vm.Groups.SelectMany(group => group.Rows)
        .Where(row => row.IsTicked).Select(row => row.Name));
    Assert.Equal(1, vm.TickedCount);
  }

  [Fact]
  public async Task TheScanRunsWhenThePageLoads_NotWhenItIsConstructed()
  {
    int scans = 0;
    PathToolScan scan = ScanOf((FirstDirectory, ["rg"]));
    ToolsSettingsViewModel vm = new(new MemoryPreferences(),
        () =>
        {
          scans++;
          return scan;
        },
        Resolver("rg"));

    Assert.Equal(0, scans);
    await vm.LoadAsync();
    Assert.Equal(1, scans);
  }

  [Fact]
  public async Task ASearchDoesNotChangeWhatIsSaved()
  {
    MemoryPreferences preferences = new();
    ToolsSettingsViewModel vm = Page(preferences, ScanOf((FirstDirectory, ["rg", "jq"])));
    await vm.LoadAsync();

    vm.Groups[0].Rows[0].IsTicked = true;
    vm.SearchText = "jq";
    await vm.SaveAsync();

    string? stored = await preferences.GetAsync(PathToolPreferences.Key, TestContext.Current.CancellationToken);
    Assert.Equal(["rg"], PathToolPreferences.Parse(stored).Value);
  }

  [Fact]
  public async Task AGroupTheUserOpenedStaysOpenAcrossASearch()
  {
    MemoryPreferences preferences = new();
    ToolsSettingsViewModel vm = Page(preferences,
        ScanOf((FirstDirectory, ["rg"]), (SecondDirectory, ["jq"])));
    await vm.LoadAsync();

    vm.Groups[0].IsExpanded = true;
    vm.SearchText = "jq";
    vm.SearchText = string.Empty;

    Assert.True(vm.Groups[0].IsExpanded);
    Assert.False(vm.Groups[1].IsExpanded);
  }

  [Fact]
  public async Task AHostWithNoPreferenceStoreStillListsThePath_AndSaysSaveCannotLand()
  {
    ToolsSettingsViewModel vm = new(null, () => ScanOf((FirstDirectory, ["rg"])), Resolver("rg"));
    await vm.LoadAsync();

    _ = Assert.Single(vm.Groups);
    await vm.SaveAsync();
    Assert.Contains("cannot be saved", vm.StatusText, StringComparison.Ordinal);
  }
}
