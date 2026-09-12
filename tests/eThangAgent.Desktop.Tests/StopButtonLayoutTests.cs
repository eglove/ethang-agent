using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>Grand-plan input-row polish (line 116): the Stop button is vertically
///     CENTERED next to the input (no longer stretched to the input's height), and
///     stays glyph-fitted - a small padded square, not a wide bar.</summary>
public class StopButtonLayoutTests
{
  [AvaloniaFact]
  public void StopButton_IsVerticallyCentered_AndGlyphFitted()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    Window window = new() { Width = 900, Height = 600, Content = new AgentView { DataContext = vm } };
    window.Show();
    Dispatcher.UIThread.RunJobs();
    AgentView view = (AgentView)window.Content;
    vm.IsBusy = true;
    Dispatcher.UIThread.RunJobs();

    TextBox input = view.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "InputBox");
    Button stop = view.GetVisualDescendants().OfType<Button>().First(b => b.Name == "StopButton");

    Assert.True(stop.IsVisible, "precondition: busy shows the stop button");
    // Vertically centered: the button's vertical center matches the input's (the
    // single-line input is 32 DIP, so equal heights here are correct); on a grown
    double inputCenter = input.Bounds.Center.Y;
    double stopCenter = stop.Bounds.Center.Y;
    Assert.Equal(inputCenter, stopCenter, 1.0);
    // glyph-fitted: small padding around "\u25A0" - wide enough to click, never a wide bar
    Assert.InRange(stop.Bounds.Width, 28, 44);
  }

  [AvaloniaFact]
  public void StopButton_StaysCentered_WhenInputGrowsMultiline()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    Window window = new() { Width = 900, Height = 600, Content = new AgentView { DataContext = vm } };
    window.Show();
    Dispatcher.UIThread.RunJobs();
    AgentView view = (AgentView)window.Content;
    vm.IsBusy = true;
    Dispatcher.UIThread.RunJobs();

    TextBox input = view.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "InputBox");
    Button stop = view.GetVisualDescendants().OfType<Button>().First(b => b.Name == "StopButton");

    input.Text = string.Join("\n", Enumerable.Range(1, 6).Select(i => "line " + i));
    Dispatcher.UIThread.RunJobs();

    Assert.True(input.Bounds.Height > 60, "precondition: input grew multiline");
    double inputCenter = input.Bounds.Center.Y;
    double stopCenter = stop.Bounds.Center.Y;
    Assert.Equal(inputCenter, stopCenter, 1.0);
    Assert.True(stop.Bounds.Height < input.Bounds.Height,
        $"expected centered stop ({stop.Bounds.Height}) shorter than grown input ({input.Bounds.Height})");
  }
}
