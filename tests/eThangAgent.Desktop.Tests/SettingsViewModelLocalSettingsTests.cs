using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>The settings modal's local-provider surface: prefill round-trips from
///     the persisted configuration, blank clears, and a malformed base URL blocks
///     the save with a named validation error while a valid one is carried.
///     Local keys share the other providers' no-whitespace boundary.</summary>
public class SettingsViewModelLocalSettingsTests
{
  [Fact]
  public void Local_Fields_Prefill_From_Constructor_And_Roundtrip_Through_The_Update()
  {
    SettingsViewModel vm = new(null, null, Zai.ACL.ZaiEndpointMode.CodingPlan,
        ToolDomain.CommitStyle.Conventional, localBaseUrl: "http://localhost:1234",
        localApiKey: "lm-studio-key");

    Assert.Equal("http://localhost:1234", vm.LocalBaseUrlText);
    Assert.Equal("lm-studio-key", vm.LocalApiKey);

    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;
    vm.SaveCommand.Execute(null);

    Assert.NotNull(saved);
    Assert.Equal("http://localhost:1234", saved.LocalBaseUrlText);
    Assert.Equal("lm-studio-key", saved.LocalApiKey);
  }

  [Fact]
  public void Blank_Local_Fields_Mean_Cleared_And_Are_Legal()
  {
    SettingsViewModel vm = new(null, null, Zai.ACL.ZaiEndpointMode.CodingPlan,
        ToolDomain.CommitStyle.Conventional);
    Assert.True(vm.CanSave);

    vm.LocalBaseUrlText = "   ";
    vm.LocalApiKey = " ";

    Assert.True(vm.CanSave);
    Assert.Null(vm.ValidationError);

    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;
    vm.SaveCommand.Execute(null);

    Assert.NotNull(saved);
    Assert.Null(saved.LocalBaseUrlText);
    Assert.Null(saved.LocalApiKey);
  }

  [Fact]
  public void NonBlank_Invalid_Local_Base_Url_Rejects_The_Save_And_Keeps_The_Dialog_Open()
  {
    SettingsViewModel vm = new(null, null, Zai.ACL.ZaiEndpointMode.CodingPlan,
        ToolDomain.CommitStyle.Conventional)
    {
      LocalBaseUrlText = "not a url",
    };

    Assert.False(vm.CanSave);
    Assert.NotNull(vm.ValidationError);
    Assert.Contains("not a valid", vm.ValidationError, StringComparison.OrdinalIgnoreCase);

    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;
    vm.SaveCommand.Execute(null); // gated — no event fires, the dialog stays open
    Assert.Null(saved);
  }

  [Fact]
  public void Valid_Local_Base_Url_Is_Accepted_And_Carried()
  {
    SettingsViewModel vm = new(null, null, Zai.ACL.ZaiEndpointMode.CodingPlan,
        ToolDomain.CommitStyle.Conventional)
    {
      LocalBaseUrlText = " http://localhost:1234 ",
    };

    Assert.True(vm.CanSave);

    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;
    vm.SaveCommand.Execute(null);

    Assert.NotNull(saved);
    Assert.Equal("http://localhost:1234", saved.LocalBaseUrlText);
  }

  [Fact]
  public void Local_Key_With_Internal_Whitespace_Is_Rejected()
  {
    SettingsViewModel vm = new(null, null, Zai.ACL.ZaiEndpointMode.CodingPlan,
        ToolDomain.CommitStyle.Conventional)
    {
      LocalApiKey = "bad key",
    };

    Assert.False(vm.CanSave);
    Assert.Contains("whitespace", vm.ValidationError, StringComparison.OrdinalIgnoreCase);
  }
}
