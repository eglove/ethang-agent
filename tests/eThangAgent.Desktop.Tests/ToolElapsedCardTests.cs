using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
}
