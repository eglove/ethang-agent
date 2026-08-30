using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
  public void Menu_Icons_Are_Centered_And_Scaled_To_Their_Buttons()
  {
    MainWindow window = CreateShellWindow();
    window.Show();
    Dispatcher.UIThread.RunJobs(); // real geometry: bounds only exist after layout

    foreach (string name in new[] { "OpenAgentMenuItem", "SessionsMenuItem", "ModelMenuItem", "EffortMenuItem", "SettingsMenuItem" })
    {
      Button item = window.GetControl<Button>(name);
      if (!item.IsVisible)
      {
        continue; // per-tab entries (model/effort) hide with no tab selected
      }

      Assert.True(item.FontSize >= 16,
          $"{name} icon must be scaled up to the button, FontSize={item.FontSize}");

      // Real rendered geometry: the glyph's center must sit on the button's center.
      // Both centers are taken in the shared transformed (root) space.
      TransformedBounds? icon = item.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.GetTransformedBounds();
      TransformedBounds? button = item.GetTransformedBounds();
      Assert.True(icon.HasValue && button.HasValue, $"{name} must render its icon glyph");
      // Bounds are local rects; Transform carries the accumulated position. Centers
      // are compared in shared root space.
      Point iconCenter = icon.Value.Bounds.Center.Transform(icon.Value.Transform);
      Point buttonCenter = button.Value.Bounds.Center.Transform(button.Value.Transform);
      double iconCenterX = iconCenter.X;
      double iconCenterY = iconCenter.Y;
      double buttonCenterX = buttonCenter.X;
      double buttonCenterY = buttonCenter.Y;
      Assert.True(Math.Abs(iconCenterX - buttonCenterX) <= 2.0,
          $"{name} icon must be horizontally centered, off by {iconCenterX - buttonCenterX}");
      Assert.True(Math.Abs(iconCenterY - buttonCenterY) <= 2.0,
          $"{name} icon must be vertically centered, off by {iconCenterY - buttonCenterY}");
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
