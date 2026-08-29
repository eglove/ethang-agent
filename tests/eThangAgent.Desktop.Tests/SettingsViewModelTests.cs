using eThangAgent.Desktop.ViewModels;
using eThangAgent.ToolDomain;
using eThangAgent.Zai.ACL;

namespace eThangAgent.Desktop.Tests;

/// <summary>The settings modal's view-model contract: prefill, blank-clears, the
///     whitespace rejection boundary, validation-driven save gating, the masked/revealed
///     mask char, and the z.ai endpoint-mode selection.</summary>
public class SettingsViewModelTests
{
  [Fact]
  public void Fields_Prefill_With_Configured_Keys()
  {
    SettingsViewModel vm = new("sk-or-v1-abc", "zai-key", ZaiEndpointMode.CodingPlan, CommitStyle.Conventional);

    Assert.Equal("sk-or-v1-abc", vm.OpenRouterKey);
    Assert.Equal("zai-key", vm.ZaiKey);
    Assert.True(vm.CanSave);
    Assert.Null(vm.ValidationError);
  }

  [Fact]
  public void Save_Raises_Update_With_Trimmed_Keys()
  {
    SettingsViewModel vm = new("  sk-or-v1-abc  ", "\tzai-key ", ZaiEndpointMode.CodingPlan, CommitStyle.Conventional);
    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;

    vm.SaveCommand.Execute(null);

    Assert.NotNull(saved);
    Assert.Equal("sk-or-v1-abc", saved.OpenRouterApiKey);
    Assert.Equal("zai-key", saved.ZaiApiKey);
  }

  [Fact]
  public void Blank_Field_Means_Cleared_And_Is_Legal()
  {
    SettingsViewModel vm = new("sk-or-v1-abc", "   ", ZaiEndpointMode.CodingPlan, CommitStyle.Conventional);
    Assert.True(vm.CanSave);

    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;
    vm.SaveCommand.Execute(null);

    Assert.NotNull(saved);
    Assert.Equal("sk-or-v1-abc", saved.OpenRouterApiKey);
    Assert.Null(saved.ZaiApiKey);
  }

  [Fact]
  public void Internal_Whitespace_Is_Rejected_And_Blocks_Save()
  {
    SettingsViewModel vm = new("not a valid key", "zai-key", ZaiEndpointMode.CodingPlan, CommitStyle.Conventional);

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
    SettingsViewModel vm = new("", "bad key with spaces", ZaiEndpointMode.CodingPlan, CommitStyle.Conventional);
    Assert.False(vm.SaveCommand.CanExecute(null));

    vm.ZaiKey = "zai-key";

    Assert.True(vm.SaveCommand.CanExecute(null));
    Assert.Null(vm.ValidationError);
  }

  [Fact]
  public void KeysVisible_Toggles_The_Mask()
  {
    SettingsViewModel vm = new(null, null, ZaiEndpointMode.CodingPlan, CommitStyle.Conventional);
    Assert.Equal('•', vm.KeyPasswordChar);

    vm.KeysVisible = true;

    Assert.Equal(default, vm.KeyPasswordChar);
  }

  [Fact]
  public void Endpoint_Mode_Prefills_And_Saves_Through_The_Update()
  {
    SettingsViewModel vm = new("key", "zai-key", ZaiEndpointMode.GeneralApi, CommitStyle.Conventional);
    Assert.Equal(ZaiEndpointMode.GeneralApi, vm.SelectedEndpointMode.Mode);

    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;
    vm.SaveCommand.Execute(null);

    Assert.NotNull(saved);
    Assert.Equal(ZaiEndpointMode.GeneralApi, saved.ZaiEndpointMode);
  }

  [Fact]
  public void Endpoint_Mode_Prefill_Coding_Plan_Offers_Both_Options()
  {
    SettingsViewModel vm = new(null, null, ZaiEndpointMode.CodingPlan, CommitStyle.Conventional);
    Assert.Equal(ZaiEndpointMode.CodingPlan, vm.SelectedEndpointMode.Mode);
    Assert.Same(vm.EndpointModes[0], vm.SelectedEndpointMode);
    Assert.Equal(2, vm.EndpointModes.Count);
    Assert.Equal(ZaiEndpointMode.GeneralApi, vm.EndpointModes[1].Mode);
  }
  [Fact]
  public void Commit_Style_Prefills_And_Saves_Through_The_Update()
  {
    SettingsViewModel vm = new("key", "zai-key", ZaiEndpointMode.CodingPlan, CommitStyle.Gitmoji);
    Assert.Equal(CommitStyle.Gitmoji, vm.SelectedCommitStyle.Style);

    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;
    vm.SaveCommand.Execute(null);

    Assert.NotNull(saved);
    Assert.Equal(CommitStyle.Gitmoji, saved.CommitStyle);
  }

  [Fact]
  public void Commit_Style_Offers_The_Three_Options_In_Display_Order()
  {
    SettingsViewModel vm = new(null, null, ZaiEndpointMode.CodingPlan, CommitStyle.Conventional);
    Assert.Equal(3, vm.CommitStyles.Count);
    Assert.Equal(CommitStyle.Conventional, vm.CommitStyles[0].Style);
    Assert.Equal(CommitStyle.Gitmoji, vm.CommitStyles[1].Style);
    Assert.Equal(CommitStyle.None, vm.CommitStyles[2].Style);
  }
}
