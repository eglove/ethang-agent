using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>The tool cards render the elapsed display in their headers: the call
///     card carries the live count-up while its tool runs, the result card the
///     frozen total. Entries are added before Show so the first layout pass
///     realizes the containers (the proven StatusBarOrderTests pattern).</summary>
public class ToolElapsedCardTests
{
  [AvaloniaFact]
  public void Tool_Call_Card_Header_Shows_The_Elapsed_Display()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    vm.Transcript.AddToolCall("read", "{}");
    Window window = new() { Content = new AgentView { DataContext = vm } };
    window.Show();

    Expander? card = FindDescendants<Expander>((Control)window.Content).FirstOrDefault();

    Assert.NotNull(card);
    Assert.Contains("0.0s", HeaderText(card), StringComparison.Ordinal);
  }

  [AvaloniaFact]
  public void Tool_Result_Card_Header_Shows_The_Frozen_Elapsed()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    vm.Transcript.AddToolCall("read", "{}");
    vm.Transcript.AddToolResult("read", "ok", "ok", false);
    Window window = new() { Content = new AgentView { DataContext = vm } };
    window.Show();

    List<Expander> cards = [.. FindDescendants<Expander>((Control)window.Content)];

    Assert.Equal(2, cards.Count);
    string resultHeader = HeaderText(cards[1]);
    Assert.Matches("0\\.\\ds", resultHeader);
  }

  [AvaloniaFact]
  public void Elapsed_Tick_Does_Not_Rebuild_Or_Collapse_An_Expanded_Call_Card()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    vm.Transcript.AddToolCall("exec", "{}");
    Window window = new() { Content = new AgentView { DataContext = vm } };
    window.Show();
    AgentView view = (AgentView)window.Content;
    Dispatcher.UIThread.RunJobs();
    Expander card = view.GetVisualDescendants().OfType<Expander>().First();
    card.IsExpanded = true;
    Dispatcher.UIThread.RunJobs();

    // The production elapsed tick mutates the entry's elapsed handle; the card
    // container must survive untouched (no rebuild, no collapse).
    ToolCallEntry entry = Assert.IsType<ToolCallEntry>(vm.Transcript.Entries[^1]);
    Assert.NotNull(entry.Elapsed);
    entry.Elapsed.Display = "1.2s";
    Dispatcher.UIThread.RunJobs();

    Expander after = view.GetVisualDescendants().OfType<Expander>().First();
    Assert.Same(card, after);
    Assert.True(after.IsExpanded, "expanded card must stay expanded across elapsed ticks");
  }

  [AvaloniaFact]
  public void Elapsed_Counter_Sits_Flush_Right_Beside_The_Chevron()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    vm.Transcript.AddToolCall("exec", "{ program }");
    Window window = new() { Content = new AgentView { DataContext = vm } };
    window.Show();
    AgentView view = (AgentView)window.Content;
    Dispatcher.UIThread.RunJobs();
    Avalonia.Controls.Primitives.ToggleButton header = view.GetVisualDescendants()
        .OfType<Avalonia.Controls.Primitives.ToggleButton>().First();
    TextBlock elapsed = header.GetVisualDescendants().OfType<TextBlock>()
        .First(t => t.Text is not null && t.Text.EndsWith('s'));
    Border chevron = header.GetVisualDescendants().OfType<Border>()
        .First(b => b.Name == "ExpandCollapseChevronBorder");
    double elapsedRight = RootX(elapsed) + elapsed.Bounds.Width;
    double chevronLeft = RootX(chevron);
    // The counter must end before the chevron and sit flush against the
    // template's own spacing: the chevron's left margin plus the Fluent header
    // presenter's right margin (the presenter inset the mirrored label inset
    // depends on). Asserted against the template's values, not magic numbers.
    Avalonia.Controls.Presenters.ContentPresenter presenter = header.GetVisualDescendants()
        .OfType<Avalonia.Controls.Presenters.ContentPresenter>()
        .First(p => p.Name == "PART_ContentPresenter");
    double spacing = chevron.Margin.Left + presenter.Margin.Right;
    Assert.True(chevronLeft >= elapsedRight - 1.0,
        $"elapsed counter must end before the chevron starts: elapsed right {elapsedRight}, chevron left {chevronLeft}");
    Assert.True(chevronLeft - spacing - elapsedRight <= 1.0,
        $"elapsed counter must sit flush against the chevron's own spacing ({spacing}): " +
        $"gap {chevronLeft - spacing - elapsedRight}");
  }

  private static IEnumerable<T> FindDescendants<T>(Control root) where T : Control
  {
    foreach (object? child in root.GetVisualChildren())
    {
      if (child is Control control)
      {
        if (control is T match)
        {
          yield return match;
        }

        foreach (T nested in FindDescendants<T>(control))
        {
          yield return nested;
        }
      }
    }
  }

  private static string HeaderText(Expander card)
    => string.Concat(FindDescendants<TextBlock>(card).Select(tb => tb.Text));

  private static double RootX(Avalonia.Visual visual)
  {
    TransformedBounds? bounds = visual.GetTransformedBounds();
    Assert.True(bounds.HasValue, "visual must be laid out");
    return (bounds.Value.Bounds.X * bounds.Value.Transform.M11) + bounds.Value.Transform.M31;
  }
}
