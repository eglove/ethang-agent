using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>The workspace settings surface: the three lists that belong to ONE workspace -
///     session files, skill directories and trusted directories - seeded from the stored
///     entries, edited through the pickers, and confirmed as one save. A duplicate path is a
///     named error rather than a second row, and the window cannot exist without a root.</summary>
[Trait("Category", "Process")]
public class WorkspaceSettingsViewModelTests
{
  private const string Root = @"C:\work\alpha";

  // ── seeding: the stored workspace entries open as rows ──

  [Fact]
  public void Rows_Seed_From_The_Stored_Workspace_Entries()
  {
    WorkspaceSettingsViewModel vm = new(Root,
        [new SessionFileEntry("AGENTS.md", true)],
        [new SessionFileEntry(@"C:\skills\ws", false)],
        [new TrustedDirectoryEntry(@"D:\shared", true, true)]);

    SessionFileRow file = Assert.Single(vm.SessionFiles);
    Assert.Equal("AGENTS.md", file.Path);
    Assert.True(file.Enabled);
    SessionFileRow skill = Assert.Single(vm.SkillDirectories);
    Assert.Equal(@"C:\skills\ws", skill.Path);
    Assert.False(skill.Enabled);
    TrustedDirectoryRow trusted = Assert.Single(vm.TrustedDirectories);
    Assert.Equal(@"D:\shared", trusted.Path);
    Assert.True(trusted.Enabled);
    Assert.True(trusted.Editable);
    Assert.Equal(Root, vm.WorkspaceRoot);
  }

  [Fact]
  public void A_Workspace_Root_Is_Required()
  {
    _ = Assert.Throws<ArgumentException>(() => new WorkspaceSettingsViewModel("   ", [], [], []));
    _ = Assert.Throws<ArgumentNullException>(() => new WorkspaceSettingsViewModel(null!, [], [], []));
  }

  // ── picking: a new row is enabled, and a duplicate is refused by name ──

  [Fact]
  public void A_Picked_Session_File_Adds_One_Enabled_Row()
  {
    WorkspaceSettingsViewModel vm = new(Root, [], [], []);

    vm.SetPickedSessionFile("AGENTS.md");

    SessionFileRow row = Assert.Single(vm.SessionFiles);
    Assert.Equal("AGENTS.md", row.Path);
    Assert.True(row.Enabled);
    Assert.Null(vm.FileError);
  }

  [Fact]
  public void A_Duplicate_Trusted_Directory_Is_Refused_And_Leaves_One_Row()
  {
    WorkspaceSettingsViewModel vm = new(Root, [], [], []);

    vm.SetPickedTrustedDirectory(@"D:\shared");
    vm.SetPickedTrustedDirectory(@"D:\shared");

    TrustedDirectoryRow row = Assert.Single(vm.TrustedDirectories);
    Assert.Equal(@"D:\shared", row.Path);
    Assert.NotNull(vm.FileError);
    Assert.Contains("already configured", vm.FileError, StringComparison.Ordinal);
    Assert.Contains(@"D:\shared", vm.FileError, StringComparison.Ordinal);
  }

  [Fact]
  public void A_Duplicate_Session_File_Is_Refused_Case_Insensitively()
  {
    WorkspaceSettingsViewModel vm = new(Root, [new SessionFileEntry(@"C:\notes\AGENTS.md", true)], [], []);

    vm.SetPickedSessionFile(@"c:\notes\agents.md");

    _ = Assert.Single(vm.SessionFiles);
    Assert.NotNull(vm.FileError);
  }

  [Fact]
  public void A_Cancelled_Pick_Changes_Nothing()
  {
    WorkspaceSettingsViewModel vm = new(Root, [], [], []);

    vm.SetPickedSessionFile(null);
    vm.SetPickedSkillDirectory("   ");
    vm.SetPickedTrustedDirectory(null);

    Assert.Empty(vm.SessionFiles);
    Assert.Empty(vm.SkillDirectories);
    Assert.Empty(vm.TrustedDirectories);
    Assert.Null(vm.FileError);
  }

  [Fact]
  public void A_Picked_Trusted_Directory_Starts_Read_Only()
  {
    WorkspaceSettingsViewModel vm = new(Root, [], [], []);

    vm.SetPickedTrustedDirectory(@"D:\shared");

    TrustedDirectoryRow row = Assert.Single(vm.TrustedDirectories);
    Assert.True(row.Enabled);
    Assert.False(row.Editable);
  }

  // ── removing and saving ──

  [Fact]
  public void Remove_Drops_Exactly_The_Named_Row()
  {
    WorkspaceSettingsViewModel vm = new(Root,
        [new SessionFileEntry(@"C:\a.md", true), new SessionFileEntry(@"C:\b.md", true)],
        [new SessionFileEntry(@"C:\skills", true)],
        [new TrustedDirectoryEntry(@"D:\shared", true, false)]);

    vm.RemoveSessionFileCommand.Execute(vm.SessionFiles[0]);
    vm.RemoveSkillDirectoryCommand.Execute(vm.SkillDirectories[0]);
    vm.RemoveTrustedDirectoryCommand.Execute(vm.TrustedDirectories[0]);

    Assert.Equal(@"C:\b.md", Assert.Single(vm.SessionFiles).Path);
    Assert.Empty(vm.SkillDirectories);
    Assert.Empty(vm.TrustedDirectories);
  }

  [Fact]
  public void Save_Carries_The_Three_Lists_And_The_Root()
  {
    WorkspaceSettingsViewModel vm = new(Root,
        [new SessionFileEntry("AGENTS.md", true)],
        [new SessionFileEntry(@"C:\skills", false)],
        [new TrustedDirectoryEntry(@"D:\shared", true, true)]);
    WorkspaceSettingsSave? captured = null;
    vm.SaveRequested += (_, save) => captured = save;

    vm.SaveCommand.Execute(null);

    Assert.NotNull(captured);
    Assert.Equal(Root, captured.WorkspaceRoot);
    Assert.Equal(new SessionFileEntry("AGENTS.md", true), Assert.Single(captured.SessionFiles));
    Assert.Equal(new SessionFileEntry(@"C:\skills", false), Assert.Single(captured.SkillDirectories));
    Assert.Equal(new TrustedDirectoryEntry(@"D:\shared", true, true),
        Assert.Single(captured.TrustedDirectories));
  }

  [Fact]
  public void Save_With_Empty_Lists_Carries_Three_Empty_Lists()
  {
    WorkspaceSettingsViewModel vm = new(Root, [], [], []);
    WorkspaceSettingsSave? captured = null;
    vm.SaveRequested += (_, save) => captured = save;

    vm.SaveCommand.Execute(null);

    Assert.NotNull(captured);
    Assert.Empty(captured.SessionFiles);
    Assert.Empty(captured.SkillDirectories);
    Assert.Empty(captured.TrustedDirectories);
  }

  // ── the window: built only over a root, and the pickers land rows ──

  [AvaloniaFact]
  public void The_Window_Cannot_Be_Built_Without_A_Workspace_Root()
  {
    _ = Assert.Throws<ArgumentException>(() => new WorkspaceSettingsViewModel("", [], [], []));
    _ = Assert.Throws<ArgumentNullException>(() => new WorkspaceSettingsWindow(null!));
  }

  [AvaloniaFact]
  public void The_Window_Renders_The_Three_Lists_And_Their_Pick_Buttons()
  {
    WorkspaceSettingsViewModel vm = new(Root,
        [new SessionFileEntry("AGENTS.md", true)],
        [new SessionFileEntry(@"C:\skills", true)],
        [new TrustedDirectoryEntry(@"D:\shared", true, false)]);
    WorkspaceSettingsWindow window = new(vm);
    window.Show();
    Dispatcher.UIThread.RunJobs();

    Assert.Same(vm.SessionFiles, window.GetControl<ItemsControl>("SessionFilesList").ItemsSource);
    Assert.Same(vm.SkillDirectories, window.GetControl<ItemsControl>("SkillDirectoriesList").ItemsSource);
    Assert.Same(vm.TrustedDirectories, window.GetControl<ItemsControl>("TrustedDirectoriesList").ItemsSource);
    _ = window.GetControl<Button>("PickSessionFileButton");
    _ = window.GetControl<Button>("PickSkillDirectoryButton");
    _ = window.GetControl<Button>("PickTrustedDirectoryButton");
    _ = window.GetControl<Button>("SaveButton");
    _ = window.GetControl<Button>("CancelButton");
    _ = Assert.Single(window.GetControl<ItemsControl>("SessionFilesList").Items);
  }

  [AvaloniaFact]
  public void The_Window_Picker_Seam_Feeds_Each_List()
  {
    WorkspaceSettingsViewModel vm = new(Root, [], [], []);
    WorkspaceSettingsWindow window = new(vm);
    window.Show();
    Dispatcher.UIThread.RunJobs();

    window.FeedPickedForTest("session-file", "AGENTS.md");
    window.FeedPickedForTest("skill-directory", @"C:\skills");
    window.FeedPickedForTest("trusted-directory", @"D:\shared");
    Dispatcher.UIThread.RunJobs();

    Assert.Equal("AGENTS.md", Assert.Single(vm.SessionFiles).Path);
    Assert.Equal(@"C:\skills", Assert.Single(vm.SkillDirectories).Path);
    Assert.Equal(@"D:\shared", Assert.Single(vm.TrustedDirectories).Path);
  }

  [AvaloniaFact]
  public void The_Window_Shows_The_Duplicate_Error()
  {
    WorkspaceSettingsViewModel vm = new(Root, [], [], []);
    WorkspaceSettingsWindow window = new(vm);
    window.Show();
    Dispatcher.UIThread.RunJobs();

    window.FeedPickedForTest("trusted-directory", @"D:\shared");
    window.FeedPickedForTest("trusted-directory", @"D:\shared");
    Dispatcher.UIThread.RunJobs();

    TextBlock error = window.GetControl<TextBlock>("FileErrorText");
    Assert.True(error.IsVisible);
    Assert.Contains("already configured", error.Text, StringComparison.Ordinal);
  }
}
