using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>The launch dialog's file-configuration surface (E moved out of
///     Settings): global and workspace session-file rows and skill-directory
///     rows, added through the native file/folder pickers (no free-text path
///     entry), checkbox enablement per row, and a confirmed choice that carries
///     both lists verbatim - what the user sees is what the preference store
///     gets. The dialog is also where the workspace root is chosen, so the
///     workspace scope is never inert here.</summary>
public class NewAgentLaunchFilesTests
{
  private static readonly IReadOnlyList<ProviderOption> Options =
      [new("openrouter", "OpenRouter")];

  private static NewAgentViewModel CreateVm() => new(Options, "openrouter");

  // ── row editing: the same SessionFileRow surface the settings editor had ──

  [Fact]
  public void Both_Scopes_Start_Empty_For_Files_And_Directories()
  {
    NewAgentViewModel vm = CreateVm();
    Assert.Empty(vm.GlobalFiles);
    Assert.Empty(vm.WorkspaceFiles);
    Assert.Empty(vm.GlobalSkillDirectories);
    Assert.Empty(vm.WorkspaceSkillDirectories);
  }

  [Fact]
  public void Prefill_Rows_Keep_Their_Checkbox_State()
  {
    NewAgentViewModel vm = new(Options, "openrouter",
        globalFiles: [new SessionFileEntry(@"C:\g\AGENTS.md", true)],
        workspaceFiles: [new SessionFileEntry(@"C:\w\NOTES.md", false)],
        globalSkillDirectories: [new SessionFileEntry(@"C:\skills\g", false)],
        workspaceSkillDirectories: [new SessionFileEntry(@"C:\skills\w", true)]);
    SessionFileRow g = Assert.Single(vm.GlobalFiles);
    Assert.Equal(@"C:\g\AGENTS.md", g.Path);
    Assert.True(g.Enabled);
    SessionFileRow w = Assert.Single(vm.WorkspaceFiles);
    Assert.False(w.Enabled);
    SessionFileRow gd = Assert.Single(vm.GlobalSkillDirectories);
    Assert.False(gd.Enabled);
    SessionFileRow wd = Assert.Single(vm.WorkspaceSkillDirectories);
    Assert.True(wd.Enabled);
  }

  [Fact]
  public void Picker_Result_Appends_Checked_Global_File_Row()
  {
    NewAgentViewModel vm = CreateVm();
    vm.SetPickedGlobalFile(@"C:\ws\AGENTS.md");
    SessionFileRow row = Assert.Single(vm.GlobalFiles);
    Assert.Equal(@"C:\ws\AGENTS.md", row.Path);
    Assert.True(row.Enabled);
  }

  [Fact]
  public void Picker_Result_Appends_Checked_Workspace_File_Row()
  {
    NewAgentViewModel vm = CreateVm();
    vm.SetPickedWorkspaceFile(@"C:\w\NOTES.md");
    SessionFileRow row = Assert.Single(vm.WorkspaceFiles);
    Assert.Equal(@"C:\w\NOTES.md", row.Path);
    Assert.True(row.Enabled);
  }

  [Fact]
  public void Picker_Result_Appends_Checked_Global_Directory_Row()
  {
    NewAgentViewModel vm = CreateVm();
    vm.SetPickedGlobalSkillDirectory(@"C:\skills\global");
    SessionFileRow row = Assert.Single(vm.GlobalSkillDirectories);
    Assert.Equal(@"C:\skills\global", row.Path);
    Assert.True(row.Enabled);
  }

  [Fact]
  public void Picker_Result_Appends_Checked_Workspace_Directory_Row()
  {
    NewAgentViewModel vm = CreateVm();
    vm.SetPickedWorkspaceSkillDirectory(@"C:\skills\ws");
    SessionFileRow row = Assert.Single(vm.WorkspaceSkillDirectories);
    Assert.Equal(@"C:\skills\ws", row.Path);
    Assert.True(row.Enabled);
  }

  [Fact]
  public void Cancelled_Pick_Keeps_The_Lists_Unchanged()
  {
    NewAgentViewModel vm = CreateVm();
    vm.SetPickedGlobalFile(null);
    vm.SetPickedWorkspaceFile("");
    vm.SetPickedGlobalSkillDirectory(" ");
    vm.SetPickedWorkspaceSkillDirectory(null);
    Assert.Empty(vm.GlobalFiles);
    Assert.Empty(vm.WorkspaceFiles);
    Assert.Empty(vm.GlobalSkillDirectories);
    Assert.Empty(vm.WorkspaceSkillDirectories);
  }

  [Fact]
  public void Duplicate_Global_File_Row_Is_Refused_With_A_Named_Error()
  {
    NewAgentViewModel vm = CreateVm();
    vm.SetPickedGlobalFile(@"C:\ws\AGENTS.md");
    vm.SetPickedGlobalFile(@"c:\ws\agents.md");
    _ = Assert.Single(vm.GlobalFiles);
    Assert.NotNull(vm.FileError);
    Assert.Contains("already configured", vm.FileError, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Duplicate_Workspace_Directory_Row_Is_Refused()
  {
    NewAgentViewModel vm = CreateVm();
    vm.SetPickedWorkspaceSkillDirectory(@"C:\skills\ws");
    vm.SetPickedWorkspaceSkillDirectory(@"C:\skills\ws");
    _ = Assert.Single(vm.WorkspaceSkillDirectories);
    Assert.NotNull(vm.FileError);
  }

  [Fact]
  public void Remove_Deletes_Exactly_Its_Row()
  {
    NewAgentViewModel vm = CreateVm();
    vm.SetPickedGlobalFile(@"C:\g\a.md");
    vm.SetPickedGlobalFile(@"C:\g\b.md");
    vm.RemoveGlobalFileCommand.Execute(vm.GlobalFiles[0]);
    SessionFileRow remaining = Assert.Single(vm.GlobalFiles);
    Assert.Equal(@"C:\g\b.md", remaining.Path);
    vm.RemoveWorkspaceFileCommand.Execute(vm.WorkspaceFiles.Count > 0 ? vm.WorkspaceFiles[0] : null!);
    vm.RemoveGlobalSkillDirectoryCommand.Execute(vm.GlobalSkillDirectories.Count > 0 ? vm.GlobalSkillDirectories[0] : null!);
    vm.RemoveWorkspaceSkillDirectoryCommand.Execute(vm.WorkspaceSkillDirectories.Count > 0 ? vm.WorkspaceSkillDirectories[0] : null!);
  }

  // ── picker request events: the view owns the native pickers ──

  [Fact]
  public void Pick_Commands_Raise_Their_Events()
  {
    NewAgentViewModel vm = CreateVm();
    List<string> raised = [];
    vm.GlobalFilePickRequested += (_, _) => raised.Add("global-file");
    vm.WorkspaceFilePickRequested += (_, _) => raised.Add("workspace-file");
    vm.GlobalSkillDirectoryPickRequested += (_, _) => raised.Add("global-dir");
    vm.WorkspaceSkillDirectoryPickRequested += (_, _) => raised.Add("workspace-dir");
    vm.PickGlobalFileCommand.Execute(null);
    vm.PickWorkspaceFileCommand.Execute(null);
    vm.PickGlobalSkillDirectoryCommand.Execute(null);
    vm.PickWorkspaceSkillDirectoryCommand.Execute(null);
    Assert.Equal(["global-file", "workspace-file", "global-dir", "workspace-dir"], raised);
  }

  // ── the confirmed choice carries both lists verbatim ──

  [Fact]
  public void Open_Carries_Both_Lists_With_Their_Checkbox_State()
  {
    NewAgentViewModel vm = CreateVm();
    vm.SetPickedGlobalFile(@"C:\g\AGENTS.md");
    vm.SetPickedWorkspaceFile(@"C:\w\NOTES.md");
    vm.SetPickedGlobalSkillDirectory(@"C:\skills\g");
    vm.SetPickedWorkspaceSkillDirectory(@"C:\skills\w");
    vm.GlobalFiles[0] = vm.GlobalFiles[0] with { Enabled = false };
    NewAgentChoice? captured = null;
    vm.OpenRequested += (_, choice) => captured = choice;
    vm.SetWorkspaceRoot(@"C:\work\demo");
    vm.OpenCommand.Execute(null);
    Assert.NotNull(captured);
    SessionFileEntry gf = Assert.Single(captured.GlobalFiles);
    Assert.Equal(@"C:\g\AGENTS.md", gf.Path);
    Assert.False(gf.Enabled);
    SessionFileEntry wf = Assert.Single(captured.WorkspaceFiles);
    Assert.Equal(@"C:\w\NOTES.md", wf.Path);
    Assert.True(wf.Enabled);
    SessionFileEntry gd = Assert.Single(captured.GlobalSkillDirectories);
    Assert.Equal(@"C:\skills\g", gd.Path);
    SessionFileEntry wd = Assert.Single(captured.WorkspaceSkillDirectories);
    Assert.Equal(@"C:\skills\w", wd.Path);
  }

  [Fact]
  public void Open_With_No_Rows_Carries_Empty_Lists()
  {
    NewAgentViewModel vm = CreateVm();
    NewAgentChoice? captured = null;
    vm.OpenRequested += (_, choice) => captured = choice;
    vm.SetWorkspaceRoot(@"C:\work\demo");
    vm.OpenCommand.Execute(null);
    Assert.NotNull(captured);
    Assert.Empty(captured.GlobalFiles);
    Assert.Empty(captured.WorkspaceFiles);
    Assert.Empty(captured.GlobalSkillDirectories);
    Assert.Empty(captured.WorkspaceSkillDirectories);
  }

  // ── the dialog's chrome: rows render, pick buttons exist ──

  [AvaloniaFact]
  public void Dialog_Renders_File_And_Directory_Sections()
  {
    NewAgentWindow window = new(Options, "openrouter");
    window.Show();
    Dispatcher.UIThread.RunJobs();
    ItemsControl global = window.GetControl<ItemsControl>("GlobalFilesList");
    Assert.NotNull(global);
    _ = window.GetControl<ItemsControl>("WorkspaceFilesList");
    _ = window.GetControl<ItemsControl>("GlobalSkillDirectoriesList");
    _ = window.GetControl<ItemsControl>("WorkspaceSkillDirectoriesList");
    _ = window.GetControl<Button>("PickGlobalFileButton");
    _ = window.GetControl<Button>("PickWorkspaceFileButton");
    _ = window.GetControl<Button>("PickGlobalSkillDirectoryButton");
    _ = window.GetControl<Button>("PickWorkspaceSkillDirectoryButton");
  }

  [AvaloniaFact]
  public void Dialog_Rows_Remove_Through_The_View()
  {
    NewAgentWindow window = new(Options, "openrouter");
    window.Show();
    Dispatcher.UIThread.RunJobs();
    NewAgentViewModel vm = (NewAgentViewModel)window.DataContext!;
    vm.SetPickedGlobalFile(@"C:\g\a.md");
    vm.SetPickedGlobalFile(@"C:\g\b.md");
    Dispatcher.UIThread.RunJobs();
    ItemsControl list = window.GetControl<ItemsControl>("GlobalFilesList");
    Assert.Equal(2, list.Items.Count);
    vm.RemoveGlobalFileCommand.Execute(vm.GlobalFiles[0]);
    Dispatcher.UIThread.RunJobs();
    _ = Assert.Single(list.Items);
  }

  [AvaloniaFact]
  public void Dialog_Picker_Events_Feed_Back_Through_The_View()
  {
    NewAgentWindow window = new(Options, "openrouter");
    window.Show();
    Dispatcher.UIThread.RunJobs();
    NewAgentViewModel vm = (NewAgentViewModel)window.DataContext!;
    // The headless shell cannot show the real picker; the view's handler is
    // exercised through its public seam instead (the same method the event
    // handler calls), proving the wiring lands rows in the bound lists.
    window.FeedPickedFileForTest("global", @"C:\g\picked.md");
    Dispatcher.UIThread.RunJobs();
    SessionFileRow row = Assert.Single(vm.GlobalFiles);
    Assert.Equal(@"C:\g\picked.md", row.Path);
  }
}
