using Avalonia.Controls;
using Avalonia.Headless.XUnit;

using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>Status-bar ordering contract: the info line reads [spinner] [phase]
///     [ctx] [provider] [model] [effort]; the Session Id control sits docked right
///     (pinned by FullWidthLayoutTests) and is not part of the info-line order.</summary>
public class StatusBarOrderTests
{
  [AvaloniaFact]
  public void Status_Bar_Renders_Phase_Before_Context()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    Window window = new() { Content = new AgentView { DataContext = vm } };
    window.Show();
    AgentView view = (AgentView)window.Content;

    StackPanel bar = view.GetControl<StackPanel>("StatusInfo");
    List<string> names = [];
    foreach (object? child in bar.Children)
    {
      string? name = child is Avalonia.StyledElement styled ? styled.Name : null;
      if (!string.IsNullOrEmpty(name))
      {
        names.Add(name);
      }
    }

    string joined = string.Join(",", names);
    Assert.True(joined.StartsWith("SpinnerText,PhaseText", StringComparison.Ordinal),
        $"expected spinner then phase first, got: {joined}");
    int ctx = names.IndexOf("ContextText");
    int phase = names.IndexOf("PhaseText");
    Assert.True(ctx > phase, $"ctx must follow phase, got: {joined}");
  }
}
