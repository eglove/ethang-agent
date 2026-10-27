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

  [AvaloniaFact]
  public void TheStatusBar_NamesTheCommittedVariant_BesideTheModel()
  {
    // THE VARIANT IS THE DECISION'S SECOND HALF, so the bar names both. A bar that showed only
    // the model would leave the user unable to tell whether their turns are routing for
    // throughput or for price - the tradeoff they explicitly chose.
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    Window window = new() { Content = new AgentView { DataContext = vm } };
    window.Show();
    AgentView view = (AgentView)window.Content;

    StackPanel bar = view.GetControl<StackPanel>("StatusInfo");
    List<string> names = [.. bar.Children
        .OfType<Avalonia.StyledElement>()
        .Select(child => child.Name)
        .OfType<string>()
        .Where(name => name.Length > 0)];

    int model = names.IndexOf("ModelIdText");
    int variant = names.IndexOf("ServingVariantText");
    Assert.True(variant > 0, $"the committed variant must appear in the bar, got: {string.Join(",", names)}");
    Assert.True(variant > model, $"the variant must follow the model it routes, got: {string.Join(",", names)}");
  }
}
