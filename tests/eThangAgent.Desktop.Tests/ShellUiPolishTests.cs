using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>Shell chrome polish: the left menu is a thin icon rail (no header, hover
///     tooltips) and tab headers render at a reduced font size.</summary>
public class ShellUiPolishTests
{
  private static MainWindow CreateShellWindow()
  {
    static Task<Result<AgentSession>> create(string root, string provider)
        => Task.FromResult(Result.Failure<AgentSession>(
            new DomainError("NoFactory", $"no factory for {root} ({provider})")));
    return new MainWindow(new MainViewModel(create));
  }

  [AvaloniaFact]
  public void Menu_Bar_Is_A_Thin_Icon_Rail_Without_A_Header()
  {
    MainWindow window = CreateShellWindow();
    window.Show();

    Border menu = window.GetControl<Border>("SideMenu");
    Assert.True(menu.Width <= 60, $"side menu must be a thin rail, width={menu.Width}");
    Assert.Null(window.FindControl<TextBlock>("MenuHeader"));

    Button[] items =
    [
            window.GetControl<Button>("OpenAgentMenuItem"),
            window.GetControl<Button>("SessionsMenuItem"),
            window.GetControl<Button>("ModelMenuItem"),
            window.GetControl<Button>("EffortMenuItem"),
            window.GetControl<Button>("SettingsMenuItem"),
        ];
    Assert.Collection(items,
        b => Assert.Equal("\uD83D\uDCC2", b.Content),
        b => Assert.Equal("\uD83D\uDCAC", b.Content),
        b => Assert.Equal("\uD83E\uDDE0", b.Content),
        b => Assert.Equal("\uD83C\uDF9A", b.Content),
        b => Assert.Equal("\u2699", b.Content));
    foreach (Button item in items)
    {
      Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(item) as string),
          $"{item.Name} must carry a hover tooltip (the old label)");
    }
  }

  [AvaloniaFact]
  public void Tab_Headers_Render_At_Reduced_Font_Size()
  {
    MainWindow window = new();
    TabControl tabs = window.GetControl<TabControl>("AgentTabs");
    Assert.Contains("compact-tabs", tabs.Classes);

    TabItem tab = new() { Header = "session" };
    _ = tabs.Items.Add(tab);
    window.Show();

    Assert.True(tab.FontSize < 13,
        $"tab header text must render smaller than the default, FontSize={tab.FontSize}");
  }
}
