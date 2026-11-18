using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;

namespace eThangAgent.Desktop.Tests;

/// <summary>The launcher's view-model: the recent list, the open-folder path, and the two
///     failure states a user can hit (a list that will not load, a row that will not delete).
///     </summary>
public sealed class LauncherViewModelTests
{
  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  [Fact]
  public async Task Load_ListsExistingAndMissingDirectories()
  {
    string existing = TestTempRoot.CreateDirectory("launcher").FullName;
    try
    {
      FakeRecentWorkspaceStore store = new();
      store.Seed(existing, "C:\\gone\\beta");
      LauncherViewModel vm = new(store, hasConfiguredProvider: true);

      await vm.LoadAsync();

      Assert.Equal(2, vm.Recents.Count);
      RecentWorkspaceRow present = vm.Recents.Single(r => r.Exists);
      RecentWorkspaceRow missing = vm.Recents.Single(r => !r.Exists);
      Assert.Equal(existing, present.Detail);
      Assert.EndsWith("not found", missing.Detail, StringComparison.Ordinal);
      Assert.Equal(0.45, missing.Dimming);
    }
    finally
    {
      Directory.Delete(existing, recursive: true);
    }
  }

  [Fact]
  public async Task OpenRecent_RaisesOnlyForAnExistingDirectory()
  {
    string existing = TestTempRoot.CreateDirectory("launcher").FullName;
    try
    {
      FakeRecentWorkspaceStore store = new();
      store.Seed(existing, "C:\\gone\\beta");
      LauncherViewModel vm = new(store, hasConfiguredProvider: true);
      await vm.LoadAsync();
      List<WorkspaceHandoff> chosen = [];
      vm.WorkspaceChosen += (_, handoff) => chosen.Add(handoff);

      RecentWorkspaceRow missing = vm.Recents.Single(r => !r.Exists);
      Assert.False(vm.OpenRecentCommand.CanExecute(missing));
      vm.OpenRecentCommand.Execute(missing);
      Assert.Empty(chosen);

      RecentWorkspaceRow present = vm.Recents.Single(r => r.Exists);
      Assert.True(vm.OpenRecentCommand.CanExecute(present));
      vm.OpenRecentCommand.Execute(present);
      WorkspaceHandoff only = Assert.Single(chosen);
      Assert.Equal(existing, only.WorkspaceRoot);
    }
    finally
    {
      Directory.Delete(existing, recursive: true);
    }
  }

  [Fact]
  public async Task Load_Failure_LeavesTheLauncherUsable()
  {
    FakeRecentWorkspaceStore store = new()
    {
      Failure = new DomainError("StorageUnavailable", "the database is gone"),
    };
    LauncherViewModel vm = new(store, hasConfiguredProvider: true);

    await vm.LoadAsync();

    Assert.Empty(vm.Recents);
    Assert.False(string.IsNullOrEmpty(vm.LoadError));
    Assert.True(vm.OpenFolderCommand.CanExecute(null));
  }

  [Fact]
  public async Task Forget_RestoresTheRowWhenTheDeleteFails()
  {
    FakeRecentWorkspaceStore store = new();
    store.Seed("C:\\work\\alpha");
    LauncherViewModel vm = new(store, hasConfiguredProvider: true);
    await vm.LoadAsync();
    RecentWorkspaceRow row = Assert.Single(vm.Recents);
    store.ForgetFailure = new DomainError("StorageUnavailable", "the database is gone");

    vm.ForgetCommand.Execute(row);
    await WaitForAsync(() => vm.LoadError is not null).ConfigureAwait(true);

    _ = Assert.Single(vm.Recents);
    Assert.False(string.IsNullOrEmpty(vm.LoadError));
  }

  [Fact]
  public async Task Forget_RemovesTheRow()
  {
    FakeRecentWorkspaceStore store = new();
    store.Seed("C:\\work\\alpha");
    LauncherViewModel vm = new(store, hasConfiguredProvider: true);
    await vm.LoadAsync();

    vm.ForgetCommand.Execute(Assert.Single(vm.Recents));
    await WaitForAsync(() => store.Recorded.Count == 0).ConfigureAwait(true);

    Assert.Empty(vm.Recents);
  }

  [Fact]
  public void ChooseFolder_RaisesTheChosenWorkspace()
  {
    string existing = TestTempRoot.CreateDirectory("launcher").FullName;
    try
    {
      LauncherViewModel vm = new(new FakeRecentWorkspaceStore(), hasConfiguredProvider: true);
      List<WorkspaceHandoff> chosen = [];
      vm.WorkspaceChosen += (_, handoff) => chosen.Add(handoff);

      vm.ChooseFolder(existing + Path.DirectorySeparatorChar);

      WorkspaceHandoff only = Assert.Single(chosen);
      Assert.Equal(existing, only.WorkspaceRoot);
    }
    finally
    {
      Directory.Delete(existing, recursive: true);
    }
  }

  [Fact]
  public void ChooseFolder_RefusesAMissingDirectory()
  {
    LauncherViewModel vm = new(new FakeRecentWorkspaceStore(), hasConfiguredProvider: true);
    List<WorkspaceHandoff> chosen = [];
    vm.WorkspaceChosen += (_, handoff) => chosen.Add(handoff);

    vm.ChooseFolder("C:\\gone\\nothing-here");

    Assert.Empty(chosen);
    Assert.False(string.IsNullOrEmpty(vm.LoadError));
  }

  [Fact]
  public void OpenFolderAndSettings_RaiseTheirRequests()
  {
    LauncherViewModel vm = new(new FakeRecentWorkspaceStore(), hasConfiguredProvider: false);
    bool folder = false;
    bool settings = false;
    vm.OpenFolderRequested += (_, _) => folder = true;
    vm.SettingsRequested += (_, _) => settings = true;

    vm.OpenFolderCommand.Execute(null);
    vm.SettingsCommand.Execute(null);

    Assert.True(folder);
    Assert.True(settings);
    Assert.False(vm.HasConfiguredProvider);
  }

  private static async Task WaitForAsync(Func<bool> condition)
  {
    for (int i = 0; i < 200 && !condition(); i++)
    {
      await Task.Delay(10, Ct).ConfigureAwait(true);
    }

    Assert.True(condition(), "the condition never became true");
  }
}
