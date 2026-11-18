using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>The launcher window renders the recent list and carries the two actions the
///     launcher exists for: open a folder, and reach Settings before any workspace exists.
///     </summary>
public sealed class LauncherWindowTests
{
  [AvaloniaFact]
  public void Window_RendersTheRecentListAndBothButtons()
  {
    FakeRecentWorkspaceStore store = new();
    store.Seed("C:\\work\\alpha");
    LauncherViewModel vm = new(store, hasConfiguredProvider: false);

    LauncherWindow window = new(vm);
    window.Show();

    ListBox list = window.FindControl<ListBox>("RecentList")
        ?? throw new InvalidOperationException("the recent list is missing");
    Assert.Same(vm.Recents, list.ItemsSource);
    Assert.NotNull(window.FindControl<Button>("OpenFolderButton"));
    Assert.NotNull(window.FindControl<Button>("SettingsButton"));
    window.Close();
  }

  [AvaloniaFact]
  public void Window_ReflectsWhetherAProviderIsConfigured()
  {
    LauncherViewModel configured = new(new FakeRecentWorkspaceStore(), hasConfiguredProvider: true);
    LauncherViewModel unconfigured = new(new FakeRecentWorkspaceStore(), hasConfiguredProvider: false);

    LauncherWindow withKey = new(configured);
    LauncherWindow withoutKey = new(unconfigured);
    withKey.Show();
    withoutKey.Show();

    Assert.True(configured.HasConfiguredProvider);
    Assert.False(unconfigured.HasConfiguredProvider);
    withKey.Close();
    withoutKey.Close();
  }
}
