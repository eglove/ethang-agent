using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;


namespace eThangAgent.Desktop.Tests;

/// <summary>Settings chrome: the flat settings list is a categorized TabControl
///     (API Keys / Skills / Models / Agents / Advanced / Git) with the validation
///     error and Save/Cancel footer shared outside the tabs. Session files and
///     skill directories live in the Open Workspace dialog, not here.</summary>
[Trait("Category", "Process")]
public class SettingsWindowTabsTests
{
  [AvaloniaFact]
  public void Settings_Renders_As_Six_Categorized_Tabs()
  {
    SettingsWindow window = new();
    window.Show();
    TabControl tabs = window.GetControl<TabControl>("SettingsTabs");
    Assert.Equal(6, tabs.Items.Count);
    Assert.Collection(tabs.Items,
        item => Assert.Equal("API Keys", Assert.IsType<TabItem>(item).Header),
        item => Assert.Equal("Skills", Assert.IsType<TabItem>(item).Header),
        item => Assert.Equal("Models", Assert.IsType<TabItem>(item).Header),
        item => Assert.Equal("Agents", Assert.IsType<TabItem>(item).Header),
        item => Assert.Equal("Advanced", Assert.IsType<TabItem>(item).Header),
        item => Assert.Equal("Git", Assert.IsType<TabItem>(item).Header));
  }

  [AvaloniaFact]
  public void Provider_Key_Fields_Live_In_The_Api_Keys_Tab()
  {
    SettingsWindow window = new();
    window.Show();
    TabControl tabs = window.GetControl<TabControl>("SettingsTabs");

    tabs.SelectedIndex = 0;
    Dispatcher.UIThread.RunJobs();
    _ = window.GetControl<TextBox>("OpenRouterKeyBox");
    _ = window.GetControl<CheckBox>("ShowKeysCheck");
    Assert.Equal(0, tabs.SelectedIndex);
  }

  [AvaloniaFact]
  public void A_Configured_Key_Is_Stated_And_The_Key_Field_Is_Hidden()
  {
    SettingsWindow signedIn = new(new SettingsValues { OpenRouterKey = "sk-or-v1-abc" });
    signedIn.Show();
    TabControl tabs = signedIn.GetControl<TabControl>("SettingsTabs");
    tabs.SelectedIndex = 0;
    Dispatcher.UIThread.RunJobs();

    Assert.False(signedIn.GetControl<TextBox>("OpenRouterKeyBox").IsVisible);
    Assert.Equal("Signed in to OpenRouter", signedIn.GetControl<TextBlock>("SignInStatusText").Text);
    Assert.True(signedIn.GetControl<Button>("SignOutButton").IsVisible);

    SettingsWindow signedOut = new(new SettingsValues());
    signedOut.Show();
    TabControl tabsOut = signedOut.GetControl<TabControl>("SettingsTabs");
    tabsOut.SelectedIndex = 0;
    Dispatcher.UIThread.RunJobs();

    Assert.True(signedOut.GetControl<TextBox>("OpenRouterKeyBox").IsVisible);
    Assert.Equal("Not signed in", signedOut.GetControl<TextBlock>("SignInStatusText").Text);
  }

  [AvaloniaFact]
  public void Compaction_Models_Live_In_The_Models_Tab()
  {
    SettingsWindow window = new();
    window.Show();
    TabControl tabs = window.GetControl<TabControl>("SettingsTabs");

    tabs.SelectedIndex = 2;
    Dispatcher.UIThread.RunJobs();
    _ = window.GetControl<ComboBox>("CompactionModelBox");
    Assert.Equal(2, tabs.SelectedIndex);
  }

  [AvaloniaFact]
  public void Commit_Style_Lives_In_The_Git_Tab()
  {
    SettingsWindow window = new();
    window.Show();
    TabControl tabs = window.GetControl<TabControl>("SettingsTabs");

    tabs.SelectedIndex = 3;
    Dispatcher.UIThread.RunJobs();
    _ = window.GetControl<ComboBox>("CommitStyleBox");
    Assert.Equal(3, tabs.SelectedIndex);
  }

  [AvaloniaFact]
  public void Footer_With_Save_And_Cancel_Sits_Outside_The_Tabs()
  {
    SettingsWindow window = new();
    window.Show();
    _ = window.GetControl<Button>("SaveButton");
    Button cancel = window.GetControl<Button>("CancelButton");
    _ = window.GetControl<TextBlock>("ValidationErrorText");

    // The footer must not live inside the TabControl (shared across tabs).
    Assert.False(IsDescendantOf(cancel, window.GetControl<TabControl>("SettingsTabs")),
        "Save/Cancel footer must be shared outside the tab control");
  }


  [AvaloniaFact]
  public void Skills_Tab_Carries_The_Registry_Target_Row()
  {
    SettingsWindow window = new(new SettingsValues());
    window.Show();
    TabControl tabs = window.GetControl<TabControl>("SettingsTabs");
    tabs.SelectedIndex = 1;
    Dispatcher.UIThread.RunJobs();
    ComboBox target = window.GetControl<ComboBox>("SkillRegistryTargetBox");
    Assert.NotNull(target);
  }

  private static bool IsDescendantOf(Avalonia.Visual node, Avalonia.Visual ancestor)
  {
    for (Avalonia.Visual? current = node; current is not null; current = current.GetVisualParent())
    {
      if (ReferenceEquals(current, ancestor))
      {
        return true;
      }
    }

    return false;
  }
}
