using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>The settings modal's view-model contract: prefill, blank-clears, the
///     whitespace rejection boundary, validation-driven save gating, and the
///     masked/revealed mask char.</summary>
public class SettingsViewModelTests
{
  [Fact]
  public void Fields_Prefill_With_Configured_Keys()
  {
    SettingsViewModel vm = new("sk-or-v1-abc", "zai-key");

    Assert.Equal("sk-or-v1-abc", vm.OpenRouterKey);
    Assert.Equal("zai-key", vm.ZaiKey);
    Assert.True(vm.CanSave);
    Assert.Null(vm.ValidationError);
  }

  [Fact]
  public void Save_Raises_Update_With_Trimmed_Keys()
  {
    SettingsViewModel vm = new("  sk-or-v1-abc  ", "\tzai-key ");
    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;

    vm.SaveCommand.Execute(null);

    Assert.NotNull(saved);
    Assert.Equal("sk-or-v1-abc", saved!.OpenRouterApiKey);
    Assert.Equal("zai-key", saved.ZaiApiKey);
  }

  [Fact]
  public void Blank_Field_Means_Cleared_And_Is_Legal()
  {
    SettingsViewModel vm = new("sk-or-v1-abc", "   ");
    Assert.True(vm.CanSave);

    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;
    vm.SaveCommand.Execute(null);

    Assert.NotNull(saved);
    Assert.Equal("sk-or-v1-abc", saved!.OpenRouterApiKey);
    Assert.Null(saved.ZaiApiKey);
  }

  [Fact]
  public void Internal_Whitespace_Is_Rejected_And_Blocks_Save()
  {
    SettingsViewModel vm = new("not a valid key", "zai-key");

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
    SettingsViewModel vm = new("", "bad key with spaces");
    Assert.False(vm.SaveCommand.CanExecute(null));

    vm.ZaiKey = "zai-key";

    Assert.True(vm.SaveCommand.CanExecute(null));
    Assert.Null(vm.ValidationError);
  }

  [Fact]
  public void KeysVisible_Toggles_The_Mask()
  {
    SettingsViewModel vm = new(null, null);
    Assert.Equal('•', vm.KeyPasswordChar);

    vm.KeysVisible = true;

    Assert.Equal(default, vm.KeyPasswordChar);
  }
}
