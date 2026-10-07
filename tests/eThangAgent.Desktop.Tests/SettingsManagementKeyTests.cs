using eThangAgent.Desktop.ViewModels;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.Tests;

/// <summary>The management key rides the settings modal exactly like the model
///     API key: prefill, blank-clears, whitespace rejection, trimmed save, and
///     DPAPI-protected persistence under its own preference key.</summary>
public class SettingsManagementKeyTests
{
  [Fact]
  public void Management_Key_Prefills_And_Saves_Trimmed()
  {
    SettingsViewModel vm = new("sk-or-v1-abc", CommitStyle.Conventional,
        skillRegistryDefaultTarget: null, openRouterManagementKey: "  sk-or-mng-1  ");
    Assert.Equal("  sk-or-mng-1  ", vm.OpenRouterManagementKey);

    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;
    vm.SaveCommand.Execute(null);

    Assert.NotNull(saved);
    Assert.Equal("sk-or-mng-1", saved.OpenRouterManagementKey);
  }

  [Fact]
  public void Blank_Management_Key_Clears_And_Is_Legal()
  {
    SettingsViewModel vm = new("sk-or-v1-abc", openRouterManagementKey: string.Empty);
    Assert.True(vm.CanSave);

    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;
    vm.SaveCommand.Execute(null);

    Assert.NotNull(saved);
    Assert.Null(saved.OpenRouterManagementKey);
  }

  [Fact]
  public void Management_Key_Internal_Whitespace_Blocks_Save()
  {
    SettingsViewModel vm = new("sk-or-v1-abc", openRouterManagementKey: "not a key");
    Assert.False(vm.CanSave);
    Assert.NotNull(vm.ValidationError);
  }

  [Fact]
  public void Management_Key_Change_Requeries_Save()
  {
    SettingsViewModel vm = new("sk-or-v1-abc", openRouterManagementKey: "not a key");
    Assert.False(vm.SaveCommand.CanExecute(null));

    vm.OpenRouterManagementKey = "sk-or-mng-1";

    Assert.True(vm.SaveCommand.CanExecute(null));
  }
}
