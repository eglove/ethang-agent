using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>Tool-card alignment: expanded tool call/result cards must sit flush-left
///     with message text - no extra Expander chrome padding or centering. The 1 DIP
///     tolerance absorbs Fluent's internal borders; anything larger is the excess the
///     plan calls out. Coordinates come from transformed bounds, which share one
///     space (the window root) for every visual.</summary>
public class ToolCardAlignmentTests
{
  private static (double CardX, double TextX) Layout(AgentView view)
  {
    Expander card = view.GetVisualDescendants().OfType<Expander>().First();
    TextBlock userText = view.GetVisualDescendants()
        .OfType<TextBlock>()
        .First(t => t.Text == "user line");
    double cardX = RootX(card);
    double textX = RootX(userText);
    return (cardX, textX);
  }

  private static double RootX(Avalonia.Visual visual)
  {
    TransformedBounds? bounds = visual.GetTransformedBounds();
    Assert.True(bounds.HasValue, "visual must be laid out");
    return (bounds.Value.Bounds.X * bounds.Value.Transform.M11) + bounds.Value.Transform.M31;
  }

  [AvaloniaFact]
  public void Tool_Cards_Are_Flush_Left_With_Message_Text()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    Window window = new() { Content = new AgentView { DataContext = vm } };
    window.Show();
    AgentView view = (AgentView)window.Content;

    vm.Transcript.AddUser("user line");
    vm.Transcript.AddToolCall("read", /*lang=json,strict*/ "{\"path\":\"a.cs\"}");
    Dispatcher.UIThread.RunJobs();

    (double cardX, double textX) = Layout(view);
    double delta = Math.Abs(cardX - textX);
    Assert.True(delta <= 1.0,
        $"tool card left edge must align with message text, delta={delta} (card {cardX}, text {textX})");
  }

  [AvaloniaFact]
  public void Tool_Result_Cards_Are_Aligned_With_Call_Cards()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    Window window = new() { Content = new AgentView { DataContext = vm } };
    window.Show();
    AgentView view = (AgentView)window.Content;

    vm.Transcript.AddToolCall("read", /*lang=json,strict*/ "{\"path\":\"a.cs\"}");
    vm.Transcript.AddToolResult("read", "ok", "content", false);
    Dispatcher.UIThread.RunJobs();

    Expander[] cards = [.. view.GetVisualDescendants().OfType<Expander>()];
    Assert.Equal(2, cards.Length);
    double callX = RootX(cards[0]);
    double resultX = RootX(cards[1]);
    Assert.True(Math.Abs(callX - resultX) <= 1.0,
        $"call and result cards must share a left edge, delta={Math.Abs(callX - resultX)}");
  }
  [AvaloniaFact]
  public void Header_Label_Inset_Mirrors_The_Chevron_Inset()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    Window window = new() { Content = new AgentView { DataContext = vm } };
    window.Show();
    AgentView view = (AgentView)window.Content;
    vm.Transcript.AddToolCall("exec", "{ program }");
    Dispatcher.UIThread.RunJobs();
    Expander card = view.GetVisualDescendants().OfType<Expander>().First();
    card.IsExpanded = true;
    Dispatcher.UIThread.RunJobs();
    Avalonia.Controls.Primitives.ToggleButton header = card.GetVisualDescendants()
        .OfType<Avalonia.Controls.Primitives.ToggleButton>().First();
    TextBlock label = header.GetVisualDescendants().OfType<TextBlock>().First();
    Border chevron = header.GetVisualDescendants().OfType<Border>()
        .First(b => b.Name == "ExpandCollapseChevronBorder");
    double labelInset = RootX(label) - RootX(card);
    double chevronInset = RootX(card) + card.Bounds.Width - (RootX(chevron) + chevron.Bounds.Width);
    Assert.True(Math.Abs(labelInset - chevronInset) <= 2.0,
        $"header label inset must mirror the chevron inset: " +
        $"label {labelInset}, chevron {chevronInset}");
  }
}
