using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>The management key field lives in the settings modal's API Keys tab,
///     masked like the model key and sharing the reveal toggle.</summary>
public class SettingsManagementKeyWindowTests
{
  [AvaloniaFact]
  public void Management_Key_Box_Lives_In_The_Api_Keys_Tab()
  {
    SettingsWindow window = new("sk-or-v1-abc", ToolDomain.CommitStyle.Conventional,
        openRouterManagementKey: "sk-or-mng-1");
    window.Show();
    TabControl tabs = window.GetControl<TabControl>("SettingsTabs");
    tabs.SelectedIndex = 0;
    Dispatcher.UIThread.RunJobs();
    TextBox box = window.GetControl<TextBox>("OpenRouterManagementKeyBox");
    Assert.Equal("sk-or-mng-1", box.Text);
    Assert.Equal(0, tabs.SelectedIndex);
  }
}
