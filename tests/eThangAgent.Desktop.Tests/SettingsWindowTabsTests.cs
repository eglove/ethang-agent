using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>Settings chrome: the flat settings list is a categorized TabControl
///     (API Keys / Models / Git) with the validation error and Save/Cancel footer
///     shared outside the tabs.</summary>
public class SettingsWindowTabsTests
{
  [AvaloniaFact]
  public void Settings_Renders_As_Three_Categorized_Tabs()
  {
    SettingsWindow window = new();
    window.Show();
    TabControl tabs = window.GetControl<TabControl>("SettingsTabs");
    Assert.Equal(3, tabs.Items.Count);
    Assert.Collection(tabs.Items,
        item => Assert.Equal("API Keys", Assert.IsType<TabItem>(item).Header),
        item => Assert.Equal("Models", Assert.IsType<TabItem>(item).Header),
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
    _ = window.GetControl<TextBox>("ZaiKeyBox");
    _ = window.GetControl<CheckBox>("ShowKeysCheck");
    Assert.Equal(0, tabs.SelectedIndex);
  }

  [AvaloniaFact]
  public void Endpoint_And_Compaction_Models_Live_In_The_Models_Tab()
  {
    SettingsWindow window = new();
    window.Show();
    TabControl tabs = window.GetControl<TabControl>("SettingsTabs");

    tabs.SelectedIndex = 1;
    Dispatcher.UIThread.RunJobs();
    _ = window.GetControl<ComboBox>("ZaiEndpointBox");
    _ = window.GetControl<ComboBox>("CompactionModelBox");
    Assert.Equal(1, tabs.SelectedIndex);
  }

  [AvaloniaFact]
  public void Commit_Style_Lives_In_The_Git_Tab()
  {
    SettingsWindow window = new();
    window.Show();
    TabControl tabs = window.GetControl<TabControl>("SettingsTabs");

    tabs.SelectedIndex = 2;
    Dispatcher.UIThread.RunJobs();
    _ = window.GetControl<ComboBox>("CommitStyleBox");
    Assert.Equal(2, tabs.SelectedIndex);
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
