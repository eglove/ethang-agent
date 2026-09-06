using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>Grand-plan layout item: the chat input and the status line span the width
///     of their container instead of a fixed 480 DIP input and a left-packed bar. The
///     input's column is star-sized, the Session Id control is docked right, and the
///     status border stretches across the row.</summary>
public class FullWidthLayoutTests
{
  [AvaloniaFact]
  public void InputBox_StarColumn_FillsRowBesideStopButton()
  {
    (AgentView view, TextBox _) = Show();
    Grid inputGrid = InputGrid(view);

    Assert.Equal(new GridLength(1, GridUnitType.Star), inputGrid.ColumnDefinitions[0].Width);
    Assert.Equal(new GridLength(1, GridUnitType.Auto), inputGrid.ColumnDefinitions[1].Width);
  }

  [AvaloniaFact]
  public void InputBox_Width_IsUnset_NotFixed480()
  {
    (AgentView _, TextBox input) = Show();

    Assert.True(double.IsNaN(input.Width), "input Width must be unset (NaN), was " + input.Width);
  }

  [AvaloniaFact]
  public void InputRow_SpansFullContainerWidth()
  {
    (AgentView view, TextBox input) = ShowAt(1100);

    double container = view.Bounds.Width;
    Grid inputGrid = InputGrid(view);
    // The row fills the container except its own symmetric 8 DIP gutters.
    Assert.Equal(container - inputGrid.Margin.Left - inputGrid.Margin.Right, inputGrid.Bounds.Width, 1.0);
    Assert.True(input.Bounds.Width > container * 0.8,
        $"input should span most of the container ({input.Bounds.Width} of {container})");
  }

  [AvaloniaFact]
  public void SessionIdButton_DocksRight_OfTheStatusBarRow()
  {
    (AgentView view, TextBox _) = Show();
    Button sessionId = SessionIdButton(view);

    Assert.Equal(Dock.Right, DockPanel.GetDock(sessionId));
  }

  [AvaloniaFact]
  public void StatusBarBorder_SpansFullContainerWidth()
  {
    (AgentView view, TextBox _) = ShowAt(1100);
    Button sessionId = SessionIdButton(view);

    Border statusBorder = view.GetVisualDescendants().OfType<Border>()
        .First(b => b.Child is DockPanel dp && dp.Children.Contains(sessionId));

    Assert.Equal(view.Bounds.Width, statusBorder.Bounds.Width, 1.0);
  }

  private static Grid InputGrid(AgentView view) => view.GetVisualDescendants().OfType<Grid>()
      .First(g => g.ColumnDefinitions.Count == 2 && g.Children.OfType<TextBox>().Any(t => t.Name == "InputBox"));

  private static Button SessionIdButton(AgentView view) => view.GetVisualDescendants().OfType<Button>()
      .First(b => b.Name == "SessionIdButton");

  private static (AgentView View, TextBox Input) Show() => ShowAt(900);

  private static (AgentView View, TextBox Input) ShowAt(double width)
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    Window window = new() { Width = width, Height = 600, Content = new AgentView { DataContext = vm } };
    window.Show();
    Dispatcher.UIThread.RunJobs();
    AgentView view = (AgentView)window.Content;
    TextBox input = view.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "InputBox");
    return (view, input);
  }
}
