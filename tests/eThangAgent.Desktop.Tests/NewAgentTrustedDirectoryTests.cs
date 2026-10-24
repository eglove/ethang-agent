using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>The launch dialog's trusted-directory surface (issue #96): workspace-scoped
/// rows added through the native folder picker, TWO independent checkboxes per row
/// (enabled and editable - approving reach and approving writes are separate decisions),
/// and a confirmed choice that carries both fields verbatim to the preference store.
/// </summary>
public sealed class NewAgentTrustedDirectoryTests
{
  private static readonly IReadOnlyList<ProviderOption> Options = [new("openrouter", "OpenRouter")];

  private static NewAgentViewModel CreateVm() => new(Options, "openrouter");

  [Fact]
  public void Rows_StartEmpty() => Assert.Empty(CreateVm().TrustedDirectories);

  [Fact]
  public void Prefill_KeepsEnabledAndEditable()
  {
    NewAgentViewModel vm = new(Options, "openrouter", trustedDirectories:
        [new TrustedDirectoryEntry(@"C:\vault", Enabled: true, Editable: true),
         new TrustedDirectoryEntry(@"C:\other", Enabled: false, Editable: false)]);

    Assert.Equal(2, vm.TrustedDirectories.Count);
    Assert.True(vm.TrustedDirectories[0].Enabled);
    Assert.True(vm.TrustedDirectories[0].Editable);
    Assert.False(vm.TrustedDirectories[1].Enabled);
    Assert.False(vm.TrustedDirectories[1].Editable);
  }

  [Fact]
  public void PickedDirectory_BecomesAnEnabledReadOnlyRow()
  {
    // The dangerous permission is never granted by a pick: a new approval starts
    // readable and read-only, and writing needs the second checkbox deliberately.
    NewAgentViewModel vm = CreateVm();
    vm.SetPickedTrustedDirectory(@"C:\vault");

    TrustedDirectoryRow row = Assert.Single(vm.TrustedDirectories);
    Assert.Equal(@"C:\vault", row.Path);
    Assert.True(row.Enabled);
    Assert.False(row.Editable);
    Assert.Null(vm.FileError);
  }

  [Fact]
  public void CancelledPick_ChangesNothing()
  {
    NewAgentViewModel vm = CreateVm();
    vm.SetPickedTrustedDirectory(null);
    vm.SetPickedTrustedDirectory("   ");

    Assert.Empty(vm.TrustedDirectories);
  }

  [Fact]
  public void DuplicateDirectory_IsANamedError()
  {
    NewAgentViewModel vm = CreateVm();
    vm.SetPickedTrustedDirectory(@"C:\vault");
    vm.SetPickedTrustedDirectory(@"C:\\VAULT");

    // One root, one approval: two rows naming it would persist two different
    // permissions and the resolver would answer with whichever came first.
    _ = Assert.Single(vm.TrustedDirectories);
    Assert.NotNull(vm.FileError);
    Assert.Contains("already configured", vm.FileError, StringComparison.Ordinal);
  }

  [Fact]
  public void Remove_DropsTheRow()
  {
    NewAgentViewModel vm = CreateVm();
    vm.SetPickedTrustedDirectory(@"C:\vault");

    // The list is MUTABLE, so the row is captured before the command removes it.
    vm.RemoveTrustedDirectoryCommand.Execute(vm.TrustedDirectories[0]);

    Assert.Empty(vm.TrustedDirectories);
  }

  [Fact]
  public void Confirm_CarriesBothFieldsToTheChoice()
  {
    NewAgentViewModel vm = CreateVm();
    vm.SetWorkspaceRoot(@"C:\ws");
    vm.SetPickedTrustedDirectory(@"C:\vault");
    // Flip the write permission the way the row's second checkbox does.
    vm.TrustedDirectories[0] = vm.TrustedDirectories[0] with { Editable = true };

    NewAgentChoice? choice = null;
    vm.OpenRequested += (_, c) => choice = c;
    vm.OpenCommand.Execute(null);

    Assert.NotNull(choice);
    TrustedDirectoryEntry entry = Assert.Single(choice.TrustedDirectories!);
    Assert.Equal(@"C:\vault", entry.Path);
    Assert.True(entry.Enabled);
    Assert.True(entry.Editable);
  }

  [Fact]
  public void Confirm_WithNoRows_CarriesAnEmptyList()
  {
    // An EMPTY list is meaningful: it clears the stored approval for that workspace.
    NewAgentViewModel vm = CreateVm();
    vm.SetWorkspaceRoot(@"C:\ws");

    NewAgentChoice? choice = null;
    vm.OpenRequested += (_, c) => choice = c;
    vm.OpenCommand.Execute(null);

    Assert.NotNull(choice);
    Assert.Empty(choice.TrustedDirectories!);
  }

  [Fact]
  public void ReplacingTheWorkspace_ReplacesTheApprovals()
  {
    // Trusted directories are workspace-scoped, so a previous root's approvals must
    // never linger over a different directory.
    NewAgentViewModel vm = new(Options, "openrouter", trustedDirectories:
        [new TrustedDirectoryEntry(@"C:\old", Enabled: true, Editable: true)]);
    _ = Assert.Single(vm.TrustedDirectories);

    vm.ReplaceWorkspaceRows([], [], [new TrustedDirectoryEntry(@"C:\new", Enabled: true, Editable: false)]);

    TrustedDirectoryRow row = Assert.Single(vm.TrustedDirectories);
    Assert.Equal(@"C:\new", row.Path);
    Assert.False(row.Editable);
  }

  [Fact]
  public void ReplacingTheWorkspace_WithNoTrustedRows_ClearsThem()
  {
    NewAgentViewModel vm = new(Options, "openrouter", trustedDirectories:
        [new TrustedDirectoryEntry(@"C:\old", Enabled: true, Editable: true)]);

    vm.ReplaceWorkspaceRows([], []);

    Assert.Empty(vm.TrustedDirectories);
  }

  [Fact]
  public void PickCommand_RaisesThePickerRequest()
  {
    NewAgentViewModel vm = CreateVm();
    bool requested = false;
    vm.TrustedDirectoryPickRequested += (_, _) => requested = true;

    vm.PickTrustedDirectoryCommand.Execute(null);

    Assert.True(requested);
  }
}
