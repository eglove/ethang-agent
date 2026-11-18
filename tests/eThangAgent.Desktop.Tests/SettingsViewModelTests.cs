using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.Tests;

/// <summary>The settings modal's view-model contract: prefill, blank-clears, the
///     whitespace rejection boundary, validation-driven save gating, and the
///     masked/revealed mask char.</summary>
public class SettingsViewModelTests
{
  [Fact]
  public void Fields_Prefill_With_Configured_Keys()
  {
    SettingsViewModel vm = new(new SettingsValues { OpenRouterKey = "sk-or-v1-abc" });

    Assert.Equal("sk-or-v1-abc", vm.OpenRouterKey);
    Assert.True(vm.CanSave);
    Assert.Null(vm.ValidationError);
  }

  [Fact]
  public void CompactionHandoff_PrefillsFromValues_AndRidesTheUpdate()
  {
    // Issue #234: the modal's checkbox must show what is stored (the host loads it at
    // startup) and carry the choice back on save.
    SettingsViewModel vm = new(new SettingsValues { CompactionHandoff = true });
    Assert.True(vm.CompactionHandoff);

    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;
    vm.CompactionHandoff = false;
    vm.SaveCommand.Execute(null);

    Assert.NotNull(saved);
    Assert.False(saved.CompactionHandoff);
  }

  [Fact]
  public void CompactionHandoff_DefaultsOff()
  {
    // Off is the shipped default: a modal opened with no stored choice must not enable a
    // behavior change the user never asked for.
    SettingsViewModel vm = new(new SettingsValues());

    Assert.False(vm.CompactionHandoff);
  }

  [Fact]
  public void Save_Raises_Update_With_Trimmed_Keys()
  {
    SettingsViewModel vm = new(new SettingsValues { OpenRouterKey = "  sk-or-v1-abc  " });
    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;

    vm.SaveCommand.Execute(null);

    Assert.NotNull(saved);
    Assert.Equal("sk-or-v1-abc", saved.OpenRouterApiKey);
  }

  [Fact]
  public void Blank_Field_Means_Cleared_And_Is_Legal()
  {
    SettingsViewModel vm = new(new SettingsValues { OpenRouterKey = string.Empty });
    Assert.True(vm.CanSave);

    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;
    vm.SaveCommand.Execute(null);

    Assert.NotNull(saved);
    Assert.Null(saved.OpenRouterApiKey);
  }

  [Fact]
  public void Internal_Whitespace_Is_Rejected_And_Blocks_Save()
  {
    SettingsViewModel vm = new(new SettingsValues { OpenRouterKey = "not a valid key" });

    Assert.False(vm.CanSave);
    Assert.NotNull(vm.ValidationError);
    Assert.Contains("whitespace", vm.ValidationError, StringComparison.OrdinalIgnoreCase);

    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;
    vm.SaveCommand.Execute(null); // gated — no event fires
    Assert.Null(saved);
  }

  [Fact]
  public void SaveCommand_Tracks_Validation_Changes()
  {
    SettingsViewModel vm = new(new SettingsValues { OpenRouterKey = "bad key with spaces" });
    Assert.False(vm.SaveCommand.CanExecute(null));

    vm.OpenRouterKey = "sk-or-v1-abc";

    Assert.True(vm.SaveCommand.CanExecute(null));
    Assert.Null(vm.ValidationError);
  }

  [Fact]
  public void KeysVisible_Toggles_The_Mask()
  {
    SettingsViewModel vm = new(new SettingsValues());
    Assert.Equal('•', vm.KeyPasswordChar);

    vm.KeysVisible = true;

    Assert.Equal(default, vm.KeyPasswordChar);
  }

  [Fact]
  public void Commit_Style_Prefills_And_Saves_Through_The_Update()
  {
    SettingsViewModel vm = new(new SettingsValues { OpenRouterKey = "key", CommitStyle = CommitStyle.Gitmoji });
    Assert.Equal(CommitStyle.Gitmoji, vm.SelectedCommitStyle.Style);

    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;
    vm.SaveCommand.Execute(null);

    Assert.NotNull(saved);
    Assert.Equal(CommitStyle.Gitmoji, saved.CommitStyle);
  }

  /// <summary>The two GLOBAL lists prefill from the opening values and ride the same save: the
  ///     Skills tab edits what applies to every workspace, and one Save writes it.</summary>
  [Fact]
  public void Global_Lists_Prefill_And_Ride_The_Update()
  {
    SettingsViewModel vm = new(new SettingsValues
    {
      GlobalSessionFiles = [new SessionFileEntry("AGENTS.md", true)],
      GlobalSkillDirectories = [new SessionFileEntry(@"C:\skills", false)],
    });

    Assert.Equal("AGENTS.md", Assert.Single(vm.GlobalSessionFiles).Path);
    Assert.Equal(@"C:\skills", Assert.Single(vm.GlobalSkillDirectories).Path);

    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;
    vm.SaveCommand.Execute(null);

    Assert.NotNull(saved);
    Assert.Equal(new SessionFileEntry("AGENTS.md", true), Assert.Single(saved.GlobalSessionFiles!));
    Assert.Equal(new SessionFileEntry(@"C:\skills", false),
        Assert.Single(saved.GlobalSkillDirectories!));
  }

  [Fact]
  public void Global_Lists_Are_Empty_When_Nothing_Is_Configured()
  {
    SettingsViewModel vm = new(new SettingsValues());
    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;

    vm.SaveCommand.Execute(null);

    Assert.Empty(vm.GlobalSessionFiles);
    Assert.Empty(vm.GlobalSkillDirectories);
    Assert.NotNull(saved);
    Assert.Empty(saved.GlobalSessionFiles!);
    Assert.Empty(saved.GlobalSkillDirectories!);
  }

  [Fact]
  public void A_Duplicate_Global_Path_Is_Refused_And_Leaves_One_Row()
  {
    SettingsViewModel vm = new(new SettingsValues());

    vm.SetPickedGlobalFile(@"C:\notes\AGENTS.md");
    vm.SetPickedGlobalFile(@"c:\notes\agents.md");

    _ = Assert.Single(vm.GlobalSessionFiles);
    Assert.NotNull(vm.FileError);
    Assert.Contains("already configured", vm.FileError, StringComparison.Ordinal);
  }

  [Fact]
  public void Global_Rows_Remove_Through_The_Commands()
  {
    SettingsViewModel vm = new(new SettingsValues
    {
      GlobalSessionFiles = [new SessionFileEntry(@"C:\a.md", true), new SessionFileEntry(@"C:\b.md", true)],
      GlobalSkillDirectories = [new SessionFileEntry(@"C:\skills", true)],
    });

    vm.RemoveGlobalFileCommand.Execute(vm.GlobalSessionFiles[0]);
    vm.RemoveGlobalSkillDirectoryCommand.Execute(vm.GlobalSkillDirectories[0]);

    Assert.Equal(@"C:\b.md", Assert.Single(vm.GlobalSessionFiles).Path);
    Assert.Empty(vm.GlobalSkillDirectories);
  }

  [Fact]
  public void Commit_Style_Offers_The_Three_Options_In_Display_Order()
  {
    SettingsViewModel vm = new(new SettingsValues());
    Assert.Equal(3, vm.CommitStyles.Count);
    Assert.Equal(CommitStyle.Conventional, vm.CommitStyles[0].Style);
    Assert.Equal(CommitStyle.Gitmoji, vm.CommitStyles[1].Style);
    Assert.Equal(CommitStyle.None, vm.CommitStyles[2].Style);
  }
}
