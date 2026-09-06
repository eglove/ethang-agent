using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>Grand-plan input-row polish: the Stop button stretches to the input's
///     current height (aligned whether the input is one line or five) and is wide
///     enough to actually read "Stop" — never a tiny unlabeled rectangle.</summary>
public class StopButtonLayoutTests
{
  [AvaloniaFact]
  public void StopButton_StretchesToInputHeight_AndHasReadableWidth()
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
    Assert.Equal(input.Bounds.Height, stop.Bounds.Height, 1.0);
    Assert.True(stop.Bounds.Width >= 56,
        $"stop button must be wide enough to read ({stop.Bounds.Width} DIP)");
  }

  [AvaloniaFact]
  public void StopButton_StaysStretched_WhenInputGrowsMultiline()
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
    Assert.Equal(input.Bounds.Height, stop.Bounds.Height, 1.0);
  }
}
