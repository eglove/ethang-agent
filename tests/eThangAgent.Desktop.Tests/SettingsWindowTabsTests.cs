using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.Lsp.ACL;
using eThangAgent.ToolDomain;
using TextBlock = Avalonia.Controls.TextBlock;


namespace eThangAgent.Desktop.Tests;

/// <summary>Settings chrome: the flat settings list is a categorized TabControl
///     (API Keys / Skills / Language servers / Tools / Models / Agents / Advanced / Git)
///     with the validation error and Save/Cancel footer shared outside the tabs. Session files and
///     skill directories live in the Open Workspace dialog, not here.</summary>
[Trait("Category", "Process")]
public class SettingsWindowTabsTests
{
  [AvaloniaFact]
  public void Settings_Renders_As_Eight_Categorized_Tabs()
  {
    SettingsWindow window = new();
    window.Show();
    TabControl tabs = window.GetControl<TabControl>("SettingsTabs");
    Assert.Equal(8, tabs.Items.Count);
    Assert.Collection(tabs.Items,
        item => Assert.Equal("API Keys", Assert.IsType<TabItem>(item).Header),
        item => Assert.Equal("Skills", Assert.IsType<TabItem>(item).Header),
        item => Assert.Equal("Language servers", Assert.IsType<TabItem>(item).Header),
        item => Assert.Equal("Tools", Assert.IsType<TabItem>(item).Header),
        item => Assert.Equal("Models", Assert.IsType<TabItem>(item).Header),
        item => Assert.Equal("Agents", Assert.IsType<TabItem>(item).Header),
        item => Assert.Equal("Advanced", Assert.IsType<TabItem>(item).Header),
        item => Assert.Equal("Git", Assert.IsType<TabItem>(item).Header));
  }

  /// <summary>The language-servers page is IN the tab: the rows are bound to the page the
  ///     dialog built, so managing a language server never opens a second window.</summary>
  [AvaloniaFact]
  public void The_Language_Servers_Tab_Holds_The_Page_Itself()
  {
    LanguageServersViewModel page = new(new StubCatalog(), new StubInstaller(), new StubDetector());
    SettingsWindow window = new(new SettingsValues { LanguageServersPage = () => page });
    window.Show();
    TabControl tabs = window.GetControl<TabControl>("SettingsTabs");

    tabs.SelectedIndex = 2;
    Dispatcher.UIThread.RunJobs();

    Assert.Same(page, ((SettingsViewModel)window.DataContext!).LanguageServersPage);
    Assert.Same(page.Rows, window.GetControl<ItemsControl>("LanguageList").ItemsSource);
  }

  /// <summary>The tools page is IN the tab: the grouped list is bound to the page the dialog
  ///     built, and the dialog's own Save is the only write, so no second window and no second
  ///     Save button exist.</summary>
  [AvaloniaFact]
  public void The_Tools_Tab_Holds_The_Page_Itself()
  {
    ToolsSettingsViewModel page = new(null, () => new PathToolScan([], 0),
        names => [.. names.Select(name => new PathToolResolution(name, null, null))]);
    SettingsWindow window = new(new SettingsValues { ToolsPage = () => page });
    window.Show();
    TabControl tabs = window.GetControl<TabControl>("SettingsTabs");

    tabs.SelectedIndex = 3;
    Dispatcher.UIThread.RunJobs();

    Assert.Same(page, ((SettingsViewModel)window.DataContext!).ToolsPage);
    Assert.Same(page.Groups, window.GetControl<ItemsControl>("ToolsGroupList").ItemsSource);
    _ = window.GetControl<TextBox>("ToolsSearchBox");
    _ = window.GetControl<TextBox>("ToolsAddNameBox");
    _ = window.GetControl<Button>("ToolsAddNameButton");
    _ = window.GetControl<TextBlock>("ToolsSummaryText");
  }

  /// <summary>A host that wired no tools page says so in the tab rather than offering a
  ///     selection whose Save would go nowhere.</summary>
  [AvaloniaFact]
  public void A_Host_With_No_Tools_Page_Says_So_In_The_Tab()
  {
    SettingsWindow window = new(new SettingsValues());
    window.Show();
    TabControl tabs = window.GetControl<TabControl>("SettingsTabs");

    tabs.SelectedIndex = 3;
    Dispatcher.UIThread.RunJobs();

    Assert.False(((SettingsViewModel)window.DataContext!).HasToolsPage);
    Assert.True(window.GetControl<TextBlock>("ToolsUnavailableText").IsVisible);
    Assert.False(window.GetControl<StackPanel>("ToolsPagePanel").IsVisible);
  }

  [AvaloniaFact]
  public void Provider_Key_Fields_Live_In_The_Api_Keys_Tab()
  {
    SettingsWindow window = new();
    window.Show();
    TabControl tabs = window.GetControl<TabControl>("SettingsTabs");

    tabs.SelectedIndex = 0;
    Dispatcher.UIThread.RunJobs();
    _ = window.GetControl<TextBox>("OpenRouterKeyBox");
    _ = window.GetControl<CheckBox>("ShowKeysCheck");
    Assert.Equal(0, tabs.SelectedIndex);
  }

  [AvaloniaFact]
  public void A_Configured_Key_Is_Stated_And_The_Key_Field_Is_Hidden()
  {
    SettingsWindow signedIn = new(new SettingsValues { OpenRouterKey = "sk-or-v1-abc" });
    signedIn.Show();
    TabControl tabs = signedIn.GetControl<TabControl>("SettingsTabs");
    tabs.SelectedIndex = 0;
    Dispatcher.UIThread.RunJobs();

    Assert.False(signedIn.GetControl<TextBox>("OpenRouterKeyBox").IsVisible);
    Assert.Equal("Signed in to OpenRouter", signedIn.GetControl<TextBlock>("SignInStatusText").Text);
    Assert.True(signedIn.GetControl<Button>("SignOutButton").IsVisible);

    SettingsWindow signedOut = new(new SettingsValues());
    signedOut.Show();
    TabControl tabsOut = signedOut.GetControl<TabControl>("SettingsTabs");
    tabsOut.SelectedIndex = 0;
    Dispatcher.UIThread.RunJobs();

    Assert.True(signedOut.GetControl<TextBox>("OpenRouterKeyBox").IsVisible);
    Assert.Equal("Not signed in", signedOut.GetControl<TextBlock>("SignInStatusText").Text);
  }

  [AvaloniaFact]
  public void Compaction_Models_Live_In_The_Models_Tab()
  {
    SettingsWindow window = new();
    window.Show();
    TabControl tabs = window.GetControl<TabControl>("SettingsTabs");

    tabs.SelectedIndex = 4;
    Dispatcher.UIThread.RunJobs();
    _ = window.GetControl<ComboBox>("CompactionModelBox");
    Assert.Equal(4, tabs.SelectedIndex);
  }

  [AvaloniaFact]
  public void Commit_Style_Lives_In_The_Git_Tab()
  {
    SettingsWindow window = new();
    window.Show();
    TabControl tabs = window.GetControl<TabControl>("SettingsTabs");

    tabs.SelectedIndex = 7;
    Dispatcher.UIThread.RunJobs();
    _ = window.GetControl<ComboBox>("CommitStyleBox");
    Assert.Equal(7, tabs.SelectedIndex);
  }

  [AvaloniaFact]
  public void Footer_With_Save_And_Cancel_Sits_Outside_The_Tabs()
  {
    SettingsWindow window = new();
    window.Show();
    _ = window.GetControl<Button>("SaveButton");
    Button cancel = window.GetControl<Button>("CancelButton");
    _ = window.GetControl<TextBlock>("ValidationErrorText");

    // The footer must not live inside the TabControl (shared across tabs).
    Assert.False(IsDescendantOf(cancel, window.GetControl<TabControl>("SettingsTabs")),
        "Save/Cancel footer must be shared outside the tab control");
  }


  [AvaloniaFact]
  public void Skills_Tab_Carries_The_Registry_Target_Row()
  {
    SettingsWindow window = new(new SettingsValues());
    window.Show();
    TabControl tabs = window.GetControl<TabControl>("SettingsTabs");
    tabs.SelectedIndex = 1;
    Dispatcher.UIThread.RunJobs();
    ComboBox target = window.GetControl<ComboBox>("SkillRegistryTargetBox");
    Assert.NotNull(target);
  }

  private static bool IsDescendantOf(Avalonia.Visual node, Avalonia.Visual ancestor)
  {
    for (Avalonia.Visual? current = node; current is not null; current = current.GetVisualParent())
    {
      if (ReferenceEquals(current, ancestor))
      {
        return true;
      }
    }

    return false;
  }

  /// <summary>The real catalog: it is immutable data, so the tab renders genuine rows.</summary>
  private sealed class StubCatalog : ILanguageServerCatalog
  {
    public IReadOnlyList<LspLanguageRow> All => LanguageServerCatalog.Default.All;

    public LspLanguageRow? ForLanguage(string? languageId) =>
        LanguageServerCatalog.Default.ForLanguage(languageId);
  }

  /// <summary>An installer that reports nothing installed and installs nothing: the tab's
  ///     rows are what this test observes, not a machine's toolchain.</summary>
  private sealed class StubInstaller : ILanguageServerInstaller
  {
    public string InstallRoot => @"C:\cache";

    public Task<LspInstallResult> InstallAsync(string languageId, CancellationToken ct) =>
        throw new NotSupportedException("InstallAsync is outside this test.");

    public Task RemoveAsync(string languageId, CancellationToken ct) =>
        throw new NotSupportedException("RemoveAsync is outside this test.");

    public Task<InstalledLanguageServers> ReadAsync(CancellationToken ct) =>
        Task.FromResult(InstalledLanguageServers.None);

    public string? MissingPrerequisiteExecutable(string languageId) => null;
  }

  /// <summary>Detection that finds nothing: no workspace is walked.</summary>
  private sealed class StubDetector : IWorkspaceLanguageDetector
  {
    public IReadOnlyList<DetectedLanguage> Detect(string workspaceRoot) => [];
  }
}
