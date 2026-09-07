using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ToolDomain;
using eThangAgent.Zai.ACL;

namespace eThangAgent.Desktop.Tests;

/// <summary>E3: the settings surface for session-start files. Two scopes (global,
///     workspace), checkbox enablement per row, absolute-path validation at add
///     time, and a save that carries BOTH lists verbatim - what the user sees is
///     what the preference store gets.</summary>
public class SettingsViewModelSessionFilesTests
{
  [Fact]
  public void Without_A_Workspace_The_Workspace_Scope_Is_Inert()
  {
    SettingsViewModel vm = CreateVm();
    Assert.False(vm.HasWorkspace);
    vm.NewWorkspaceFile = @"C:\ws\NOTES.md";
    vm.AddWorkspaceFileCommand.Execute(null);
    Assert.Empty(vm.WorkspaceFiles);
    // Save carries no workspace files: nothing may be persisted under a blank key.
    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;
    vm.SaveCommand.Execute(null);
    Assert.NotNull(saved);
    Assert.Null(saved.WorkspaceRoot);
    Assert.Null(saved.WorkspaceFiles);
  }

  [Fact]
  public void With_A_Workspace_Rows_Prefill_And_Save_Carries_The_Root()
  {
    SettingsViewModel vm = CreateVm(
        workspaceRoot: @"C:\proj\demo",
        workspace: [new SessionFileEntry(@"C:\proj\demo\NOTES.md", true)]);
    Assert.True(vm.HasWorkspace);
    Assert.Equal(@"C:\proj\demo", vm.WorkspaceRoot);
    SessionFileRow w = Assert.Single(vm.WorkspaceFiles);
    Assert.True(w.Enabled);
    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;
    vm.SaveCommand.Execute(null);
    Assert.NotNull(saved);
    Assert.Equal(@"C:\proj\demo", saved.WorkspaceRoot);
    Assert.NotNull(saved.WorkspaceFiles);
    SessionFileEntry workspaceEntry = saved.WorkspaceFiles[0];
    _ = workspaceEntry;
  }

  private static SettingsViewModel CreateVm(
      IReadOnlyList<SessionFileEntry>? global = null,
      IReadOnlyList<SessionFileEntry>? workspace = null,
      string? workspaceRoot = null) => new(
      "sk-or-test", null, ZaiEndpointMode.CodingPlan, CommitStyle.Conventional,
      globalFiles: global, workspaceFiles: workspace,
      workspaceRoot: workspaceRoot);

  [Fact]
  public void Without_Configured_Files_Both_Scopes_Start_Empty()
  {
    SettingsViewModel vm = CreateVm();
    Assert.Empty(vm.GlobalFiles);
    Assert.Empty(vm.WorkspaceFiles);
  }

  [Fact]
  public void Configured_Entries_Prefill_Rows_With_Their_Checkbox_State()
  {
    SettingsViewModel vm = CreateVm(
        global: [new SessionFileEntry(@"C:\g\AGENTS.md", true)],
        workspace: [new SessionFileEntry(@"C:\w\NOTES.md", false)],
        workspaceRoot: @"C:\w");
    SessionFileRow g = Assert.Single(vm.GlobalFiles);
    Assert.Equal(@"C:\g\AGENTS.md", g.Path);
    Assert.True(g.Enabled);
    SessionFileRow w = Assert.Single(vm.WorkspaceFiles);
    Assert.Equal(@"C:\w\NOTES.md", w.Path);
    Assert.False(w.Enabled);
  }

  [Fact]
  public void Add_Global_File_Appends_A_Row_And_Clears_The_Entry_Field()
  {
    SettingsViewModel vm = CreateVm();
    vm.NewGlobalFile = @"C:\ws\AGENTS.md";
    vm.AddGlobalFileCommand.Execute(null);
    SessionFileRow row = Assert.Single(vm.GlobalFiles);
    Assert.Equal(@"C:\ws\AGENTS.md", row.Path);
    Assert.True(row.Enabled);
    Assert.Equal(string.Empty, vm.NewGlobalFile);
  }

  [Fact]
  public void Add_Rejects_Relative_Paths_With_A_Named_Error()
  {
    SettingsViewModel vm = CreateVm();
    vm.NewGlobalFile = "relative/notes.md";
    vm.AddGlobalFileCommand.Execute(null);
    Assert.Empty(vm.GlobalFiles);
    Assert.Contains("absolute", vm.ValidationError, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Add_Workspace_File_Lands_In_The_Workspace_Scope()
  {
    SettingsViewModel vm = CreateVm(workspaceRoot: @"C:\ws");
    vm.NewWorkspaceFile = @"C:\ws\NOTES.md";
    vm.AddWorkspaceFileCommand.Execute(null);
    _ = Assert.Single(vm.WorkspaceFiles);
    Assert.Empty(vm.GlobalFiles);
  }

  [Fact]
  public void Remove_Deletes_Exactly_Its_Row()
  {
    SettingsViewModel vm = CreateVm(global: [
        new SessionFileEntry(@"C:\g\a.md", true),
        new SessionFileEntry(@"C:\g\b.md", true)]);
    vm.RemoveGlobalFileCommand.Execute(vm.GlobalFiles[0]);
    SessionFileRow remaining = Assert.Single(vm.GlobalFiles);
    Assert.Equal(@"C:\g\b.md", remaining.Path);
  }

  [Fact]
  public void Save_Carries_Both_Lists_With_Their_Checkbox_State()
  {
    SettingsViewModel vm = CreateVm(
        global: [new SessionFileEntry(@"C:\g\AGENTS.md", true)],
        workspace: [new SessionFileEntry(@"C:\w\NOTES.md", false)],
        workspaceRoot: @"C:\w");
    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;
    vm.SaveCommand.Execute(null);
    Assert.NotNull(saved);
    Assert.NotNull(saved.GlobalFiles);
    Assert.NotNull(saved.WorkspaceFiles);
    Assert.True(Assert.Single(saved.GlobalFiles).Enabled);
    Assert.False(Assert.Single(saved.WorkspaceFiles).Enabled);
  }
}
