using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.Tests;

/// <summary>The Tools tab and the dialog's one Save: ticking a tool in the tab is what the
///     settings dialog writes, an untouched tab changes nothing, and the shell persists the
///     selection as the one global preference the prompt reads.</summary>
[Trait("Category", "Process")]
public class PathToolsSettingsTests
{
  private const string Directory = @"C:\tools\first";

  private sealed class MemoryPreferences : IAppPreferenceStore
  {
    public Dictionary<string, string> Stored { get; } = [with(StringComparer.Ordinal)];

    public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(Stored.TryGetValue(key, out string? value) ? value : null);

    public Task<bool> SetAsync(string key, string value, CancellationToken ct = default)
    {
      Stored[key] = value;
      return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(Stored.Remove(key));
  }

  private sealed class FakeKeyProtector : IApiKeyProtector
  {
    public string Protect(string apiKey) => "protected:" + apiKey;

    public string? Unprotect(string storedValue) =>
        storedValue.StartsWith("protected:", StringComparison.Ordinal) ? storedValue[10..] : null;
  }

  private static PathToolScan ScanOf(params string[] names) =>
      new([new PathToolDirectory(Directory,
          [.. names.Select(name => new PathTool(name, Directory,
              Path.Combine(Directory, name + ".exe"), PathToolKind.ConsoleProgram))])], 0);

  private static ToolsSettingsViewModel Page(params string[] names) =>
      new(null, () => ScanOf(names),
          names => [.. names.Select(name => new PathToolResolution(name, null, null))]);

  /// <summary>Ticking a tool in the tab is what Save writes: the selection travels out of the
  ///     dialog inside the settings update, so a cancel discards it with everything else.</summary>
  [Fact]
  public async Task TheDialogsSaveCarriesTheToolsTabsSelection()
  {
    ToolsSettingsViewModel page = Page("rg", "jq");
    SettingsViewModel vm = new(new SettingsValues { ToolsPage = () => page });
    await vm.LoadPagesAsync();

    page.Groups[0].Rows[0].IsTicked = true;
    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;
    vm.SaveCommand.Execute(null);

    Assert.NotNull(saved);
    Assert.Equal(["rg"], PathToolPreferences.Parse(saved.PathToolsText).Value);
  }

  /// <summary>An empty selection is a null, which the shell persists as a delete: the store
  ///     refuses a blank value, so 'nothing ticked' is expressed by the absence of the row.</summary>
  [Fact]
  public async Task AnEmptySelectionSavesAsNoValueAtAll()
  {
    ToolsSettingsViewModel page = Page("rg");
    SettingsViewModel vm = new(new SettingsValues { ToolsPage = () => page });
    await vm.LoadPagesAsync();

    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;
    vm.SaveCommand.Execute(null);

    Assert.NotNull(saved);
    Assert.Null(saved.PathToolsText);
  }

  /// <summary>A page that never read the PATH has no rows, so its empty answer must not be
  ///     mistaken for an empty selection: the value the dialog opened with is written back.
  /// </summary>
  [Fact]
  public void AnUnreadPageWritesBackTheSelectionTheDialogOpenedWith()
  {
    ToolsSettingsViewModel page = Page("rg");
    SettingsViewModel vm = new(new SettingsValues
    {
      ToolsPage = () => page,
      PathToolsText = "[\"rg\"]",
    });

    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;
    vm.SaveCommand.Execute(null);

    Assert.NotNull(saved);
    Assert.Equal(["rg"], PathToolPreferences.Parse(saved.PathToolsText).Value);
  }

  /// <summary>The shell is what persists the selection: the update reaches the one global
  ///     preference the session factory and the prompt both read.</summary>
  [Fact]
  public async Task TheShellPersistsTheSelectionAsTheGlobalPreference()
  {
    MemoryPreferences preferences = new();
    MainViewModel shell = new(null, new MainViewModelOptions
    {
      Preferences = preferences,
      Settings = new AgentSettings(
          new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
          new AgentDomain.AgentRunOptions(null, 2)),
      SessionFactory = new AgentSessionFactory(new AgentSettings(
          new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
          new AgentDomain.AgentRunOptions(null, 2))),
      ApiKeyProtector = new FakeKeyProtector(),
    });

    await shell.ApplySettingsAsync(new SettingsUpdate(null, CommitStyle.Conventional,
        PathToolsText: "[\"rg\",\"jq\"]"));

    Assert.Equal(["rg", "jq"],
        PathToolPreferences.Parse(preferences.Stored[PathToolPreferences.Key]).Value);
  }

  /// <summary>Clearing the selection deletes the preference rather than storing a blank, so a
  ///     user who unticks everything returns to the prompt a fresh install has.</summary>
  [Fact]
  public async Task ClearingTheSelectionDeletesThePreference()
  {
    MemoryPreferences preferences = new();
    preferences.Stored[PathToolPreferences.Key] = "[\"rg\"]";
    MainViewModel shell = new(null, new MainViewModelOptions
    {
      Preferences = preferences,
      Settings = new AgentSettings(
          new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
          new AgentDomain.AgentRunOptions(null, 2)),
      SessionFactory = new AgentSessionFactory(new AgentSettings(
          new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
          new AgentDomain.AgentRunOptions(null, 2))),
      ApiKeyProtector = new FakeKeyProtector(),
    });

    await shell.ApplySettingsAsync(new SettingsUpdate(null, CommitStyle.Conventional,
        PathToolsText: null));

    Assert.False(preferences.Stored.ContainsKey(PathToolPreferences.Key));
  }
}
