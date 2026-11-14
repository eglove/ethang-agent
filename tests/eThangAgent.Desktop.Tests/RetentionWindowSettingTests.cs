using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>The retention window in the settings modal (issue #246): a window a user can change,
///     and a way to switch retention off entirely.
///     <para>DISABLING IS ITS OWN LEGAL ENTRY. Every other duration on that page rejects zero, and
///     this one must accept it: refusing the only value that means 'keep everything' would leave a
///     user no way to stop the sweep except deleting a preference row by hand.</para></summary>
public class RetentionWindowSettingTests
{
  [Fact]
  public void AnEmptyWindow_IsTheDefault_AndSaves()
  {
    SettingsViewModel vm = new(new SettingsValues());

    Assert.Null(vm.ValidationError);
    Assert.True(vm.CanSave);
  }

  [Fact]
  public void Zero_DisablesRetention_AndIsALegalEntry()
  {
    SettingsViewModel vm = new(new SettingsValues { RetentionWindowText = "0" });

    Assert.Null(vm.ValidationError);
  }

  [Theory]
  [InlineData("30.00:00:00")]
  [InlineData("7.00:00:00")]
  [InlineData("01:00:00")]
  public void AWindowWithUnits_IsAccepted(string text)
  {
    SettingsViewModel vm = new(new SettingsValues { RetentionWindowText = text });

    Assert.Null(vm.ValidationError);
  }

  [Theory]
  [InlineData("30")]
  [InlineData("-30.00:00:00")]
  [InlineData("whenever")]
  public void AWindowWithoutLegalUnits_IsRefused(string text)
  {
    SettingsViewModel vm = new(new SettingsValues { RetentionWindowText = text });

    Assert.NotNull(vm.ValidationError);
    Assert.Contains("Retention window", vm.ValidationError, StringComparison.Ordinal);
    Assert.False(vm.CanSave);
  }

  /// <summary>The save carries the new field through, so the value reaches the store the sweep
  ///     reads rather than dying in the dialog.</summary>
  [Fact]
  public void TheSave_CarriesTheWindow()
  {
    SettingsViewModel vm = new(new SettingsValues { RetentionWindowText = "7.00:00:00" });
    SettingsUpdate? captured = null;
    vm.SaveRequested += (_, update) => captured = update;

    vm.SaveCommand.Execute(parameter: null);

    Assert.NotNull(captured);
    Assert.Equal("7.00:00:00", captured.RetentionWindowText);
  }
}
