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
[Trait("Category", "Process")]
public class ShellUiPolishTests
{
  private static MainViewModel CreateShellWindowVm()
  {
    static Task<Result<AgentSession>> create(string root, string provider)
        => Task.FromResult(Result.Success(FakeSession(root)));
    return new MainViewModel(create);
  }

  private static MainWindow CreateShellWindow() => new(CreateShellWindowVm());

  /// <summary>A shell bound to one workspace, the shape the launcher hands the main window.</summary>
  private static MainViewModel CreateBoundShellVm(string? workspaceRoot)
  {
    static Task<Result<AgentSession>> create(string root, string provider)
        => Task.FromResult(Result.Success(FakeSession(root)));
    return new MainViewModel(create, new MainViewModelOptions { WorkspaceRoot = workspaceRoot });
  }

  private static MainWindow CreateBoundShellWindow(string? workspaceRoot)
      => new(CreateBoundShellVm(workspaceRoot));

  private static AgentSession FakeSession(string root)
      => new(
          new ServiceCollection().BuildServiceProvider(),
          AgentDomain.AgentId.NewId(),
          new ConversationDomain.Conversation(),
          Handler: null!,
          Lifecycle: new RootSessionLifecycle(new TestFixtures.StubStore()),
          Model: ModelDomain.ModelConfig.Create("test/model", 128, 0.1f, 8192).Value!,
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

    // NO NEW AGENT ENTRY: the window's '+' control is the only way to start an agent, so the
    // rail carries no second control that could drift from it.
    Assert.Null(window.FindControl<Button>("OpenAgentMenuItem"));
    Button[] items =
    [
            window.GetControl<Button>("SessionsMenuItem"),
            window.GetControl<Button>("ModelSettingsMenuItem"),
            window.GetControl<Button>("SettingsMenuItem"),
        ];
    Assert.Collection(items,
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

    foreach (string name in new[]
    {
      "SessionsMenuItem", "ModelSettingsMenuItem", "SettingsMenuItem",
      "CloseWorkspaceMenuItem", "WorkspaceSettingsMenuItem",
    })
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

  /// <summary>Regression: the MCP Servers rail button is guarded on HasSelectedTab,
  ///     so its command must re-query when the selection changes. A button Avalonia
  ///     never re-evaluates keeps its startup disabled state forever — the bug
  ///     report's greyed-out button. Driven through a real window: the open selects
  ///     the tab, and the button must re-enable from the command's own
  ///     CanExecuteChanged, with no further notification.</summary>
  [AvaloniaFact]
  public async Task Rail_Tab_Buttons_Are_Enabled_When_A_Tab_Is_Selected()
  {
    MainViewModel shell = CreateShellWindowVm();
    MainWindow window = new(shell);
    window.Show();
    Dispatcher.UIThread.RunJobs(); // let the Command bindings resolve

    Button mcp = window.GetControl<Button>("McpServersMenuItem");
    Assert.False(mcp.IsEffectivelyEnabled);

    // The same path the rail's New Agent entry drives: a successful open selects the
    // tab, and the button must re-enable from the command's own CanExecuteChanged
    // — with no further notification.
    _ = await shell.OpenAgentAsync(@"C:\work\alpha", "openrouter").ConfigureAwait(true);
    Dispatcher.UIThread.RunJobs();

    Assert.True(mcp.IsEffectivelyEnabled, "McpServersMenuItem must re-enable once a tab is selected");
  }

  [AvaloniaFact]
  public void WindowTitle_NamesTheBoundWorkspace_AndIsBareWhenUnbound()
  {
    Assert.Equal("eThang Agent \u2014 alpha", CreateBoundShellWindow(@"C:\work\alpha").Title);
    Assert.Equal("eThang Agent", CreateBoundShellWindow(null).Title);
  }

  /// <summary>The window's '+' is the only start control, and it renders only where there is
  ///     a workspace to start an agent in - a window bound to nothing has nowhere to put one.
  ///     </summary>
  [AvaloniaFact]
  public void TheAddControl_IsVisibleOnlyWhileAWorkspaceIsBound()
  {
    MainWindow bound = CreateBoundShellWindow(@"C:\work\alpha");
    bound.Show();
    Dispatcher.UIThread.RunJobs();
    Button add = bound.GetControl<Button>("AddAgentButton");
    Assert.Contains("Start another agent", ToolTip.GetTip(add) as string, StringComparison.Ordinal);
    Assert.True(add.IsVisible, "the + must render in a bound window");

    MainWindow unbound = CreateBoundShellWindow(null);
    unbound.Show();
    Dispatcher.UIThread.RunJobs();
    Assert.False(unbound.GetControl<Button>("AddAgentButton").IsVisible,
        "the + must be hidden with no workspace bound");
  }

  /// <summary>The workspace settings entry is visible only while a workspace is bound, and it
  ///     is the only rail entry that opens the window editing this workspace's lists.</summary>
  [AvaloniaFact]
  public void Rail_WorkspaceSettingsEntry_IsVisibleOnlyWhileAWorkspaceIsBound()
  {
    MainWindow bound = CreateBoundShellWindow(@"C:\work\alpha");
    bound.Show();
    Dispatcher.UIThread.RunJobs();
    Button workspaceSettings = bound.GetControl<Button>("WorkspaceSettingsMenuItem");
    Assert.True(workspaceSettings.IsVisible);
    Assert.True(workspaceSettings.IsEffectivelyEnabled);
    Assert.Contains("Workspace settings", ToolTip.GetTip(workspaceSettings) as string,
        StringComparison.Ordinal);

    MainWindow unbound = CreateBoundShellWindow(null);
    unbound.Show();
    Dispatcher.UIThread.RunJobs();
    Assert.False(unbound.GetControl<Button>("WorkspaceSettingsMenuItem").IsVisible,
        "the workspace settings entry must be hidden with no workspace bound");
  }

  [AvaloniaFact]
  public void EmptyState_NamesTheWorkspace_AndNoLongerPointsAtOpenWorkspace()
  {
    MainWindow window = CreateBoundShellWindow(@"C:\work\alpha");
    window.Show();
    Dispatcher.UIThread.RunJobs();

    StackPanel emptyState = window.GetControl<StackPanel>("EmptyState");
    Assert.True(emptyState.IsVisible);
    string text = window.GetControl<TextBlock>("EmptyStateText").Text ?? string.Empty;
    Assert.StartsWith("No agent is open.", text, StringComparison.Ordinal);
    Assert.Contains(@"C:\work\alpha", text, StringComparison.Ordinal);
    Assert.DoesNotContain("Open Workspace", text, StringComparison.Ordinal);
  }

  /// <summary>The window renders no tab strip at all: one window is one workspace and one
  ///     agent grid, so there is no strip to switch between agents.</summary>
  [AvaloniaFact]
  public void TheWindow_RendersNoTabStrip()
  {
    MainWindow window = new();
    window.Show();
    Dispatcher.UIThread.RunJobs();

    Assert.Null(window.FindControl<TabControl>("AgentTabs"));
    Assert.Empty(window.GetVisualDescendants().OfType<TabControl>());
  }
}
