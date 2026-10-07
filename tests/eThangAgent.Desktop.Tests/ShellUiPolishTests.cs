using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>Shell chrome polish: the left menu is a thin icon rail (no header, hover
///     tooltips) and tab headers render at a reduced font size.</summary>
public class ShellUiPolishTests
{
  private static MainViewModel CreateShellWindowVm()
  {
    static Task<Result<AgentSession>> create(string root, string provider)
        => Task.FromResult(Result.Success(FakeSession(root)));
    return new MainViewModel(create);
  }

  private static MainWindow CreateShellWindow() => new(CreateShellWindowVm());

  private static AgentSession FakeSession(string root)
      => new(
          new ServiceCollection().BuildServiceProvider(),
          AgentDomain.AgentId.NewId(),
          new ConversationDomain.Conversation(),
          Handler: null!,
          Lifecycle: new RootSessionLifecycle(new TestFixtures.StubStore()),
          Model: ModelDomain.ModelConfig.Create("test/model", null, 128, 0.1f, 8192).Value!,
          WorkspaceRoot: root,
          ProviderName: "openrouter",
          Inbox: new AgentDomain.BoundedAgentMailbox(),
          ChildRuntime: new TestFixtures.StubAgentRuntime());

  [AvaloniaFact]
  public void Menu_Bar_Is_A_Thin_Icon_Rail_Without_A_Header()
  {
    MainWindow window = CreateShellWindow();
    window.Show();

    Border menu = window.GetControl<Border>("SideMenu");
    Assert.True(menu.Width <= 60, $"side menu must be a thin rail, width={menu.Width}");
    Assert.Null(window.FindControl<TextBlock>("MenuHeader"));

    // Grand-plan item 117: the Model and Effort rail entries are gone — their
    // choices live in the Model Settings window, the rail's per-tab entry.
    Assert.Null(window.FindControl<Button>("ModelMenuItem"));
    Assert.Null(window.FindControl<Button>("EffortMenuItem"));

    Button[] items =
    [
            window.GetControl<Button>("OpenAgentMenuItem"),
            window.GetControl<Button>("SessionsMenuItem"),
            window.GetControl<Button>("ModelSettingsMenuItem"),
            window.GetControl<Button>("SettingsMenuItem"),
        ];
    Assert.Collection(items,
        b => Assert.Equal("\uD83D\uDCC2", b.Content),
        b => Assert.Equal("\uD83D\uDCAC", b.Content),
        b => Assert.Equal("\uD83C\uDF9B", b.Content),
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

    foreach (string name in new[] { "OpenAgentMenuItem", "SessionsMenuItem", "ModelSettingsMenuItem", "SettingsMenuItem" })
    {
      Button item = window.GetControl<Button>(name);
      if (!item.IsVisible)
      {
        continue; // per-tab entries (model settings) hide with no tab selected
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

  /// <summary>Regression: the Links and MCP Servers rail buttons are guarded on
  ///     HasSelectedTab, so their commands must re-query when the selection changes.
  ///     A button Avalonia never re-evaluates keeps its startup disabled state
  ///     forever — the bug report's greyed-out pair. Driven through a real window:
  ///     the open selects the tab, and the buttons must re-enable from the command's
  ///     own CanExecuteChanged, with no further notification.</summary>
  [AvaloniaFact]
  public async Task Rail_Tab_Buttons_Are_Enabled_When_A_Tab_Is_Selected()
  {
    MainViewModel shell = CreateShellWindowVm();
    MainWindow window = new(shell);
    window.Show();
    Dispatcher.UIThread.RunJobs(); // let the Command bindings resolve

    Button links = window.GetControl<Button>("LinksMenuItem");
    Button mcp = window.GetControl<Button>("McpServersMenuItem");
    Assert.False(links.IsEffectivelyEnabled);
    Assert.False(mcp.IsEffectivelyEnabled);

    // The same path the new-agent dialog drives: a successful open selects the
    // tab, and the buttons must re-enable from the command's own CanExecuteChanged
    // — with no further notification.
    _ = await shell.OpenAgentAsync(@"C:\work\alpha", "openrouter").ConfigureAwait(true);
    Dispatcher.UIThread.RunJobs();

    Assert.True(links.IsEffectivelyEnabled, "LinksMenuItem must re-enable once a tab is selected");
    Assert.True(mcp.IsEffectivelyEnabled, "McpServersMenuItem must re-enable once a tab is selected");
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
