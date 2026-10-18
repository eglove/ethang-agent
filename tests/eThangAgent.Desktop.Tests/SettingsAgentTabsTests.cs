using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>The settings modal's Agents and Advanced tabs: the view-model carries
///     the sub-agent and base-URL knobs, validates them with the same strict rules
///     the loader binds with (positive integer, unit-carrying durations, non-negative
///     wrap-up attempts, absolute URIs), and the confirmed update transports every
///     field for persistence.</summary>
public class SettingsAgentTabsTests
{
  [Fact]
  public void Save_CarriesAgentAndAdvancedFields()
  {
    SettingsViewModel vm = new(new SettingsValues
    {
      MaxConcurrentAgentsText = " 6 ",
      DefaultModelText = " glm-5.3 ",
      RemoteHost = true,
      WatchdogTickText = " 00:00:02 ",
      WatchdogIdleText = " 00:15:00 ",
      WatchdogWrapUpText = " 2 ",
      OpenRouterBaseUrlText = " https://openrouter.ai ",
    });

    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;
    vm.SaveCommand.Execute(null);

    Assert.NotNull(saved);
    Assert.Equal("6", saved.MaxConcurrentAgentsText);
    Assert.Equal("glm-5.3", saved.DefaultModelText);
    Assert.True(saved.RemoteHost);
    Assert.Equal("00:00:02", saved.WatchdogTickText);
    Assert.Equal("00:15:00", saved.WatchdogIdleText);
    Assert.Equal("2", saved.WatchdogWrapUpText);
    Assert.Equal("https://openrouter.ai", saved.OpenRouterBaseUrlText);
  }

  [Fact]
  public void ValidationError_RejectsBadMaxConcurrent()
  {
    SettingsViewModel vm = new(new SettingsValues { MaxConcurrentAgentsText = "0" });

    Assert.False(vm.CanSave);
    Assert.NotNull(vm.ValidationError);
    Assert.Contains("positive integer", vm.ValidationError, StringComparison.Ordinal);
  }

  [Fact]
  public void ValidationError_RejectsBareIntegerDuration()
  {
    SettingsViewModel vm = new(new SettingsValues { WatchdogTickText = "5" });

    Assert.NotNull(vm.ValidationError);
    Assert.Contains("would bind as days", vm.ValidationError, StringComparison.Ordinal);
  }

  [Fact]
  public void ValidationError_RejectsBadAdvancedUrl()
  {
    SettingsViewModel vm = new(new SettingsValues { OpenRouterBaseUrlText = "nope" });

    Assert.NotNull(vm.ValidationError);
    Assert.Contains("absolute URI", vm.ValidationError, StringComparison.Ordinal);
  }

  [Fact]
  public void Blank_Fields_Are_Legal_Defaults()
  {
    SettingsViewModel vm = new(new SettingsValues
    {
      MaxConcurrentAgentsText = "   ",
      DefaultModelText = " ",
      WatchdogTickText = "",
      WatchdogIdleText = "  ",
      WatchdogWrapUpText = " ",
      OpenRouterBaseUrlText = "   ",
    });

    Assert.True(vm.CanSave);
    Assert.Null(vm.ValidationError);
  }
}
