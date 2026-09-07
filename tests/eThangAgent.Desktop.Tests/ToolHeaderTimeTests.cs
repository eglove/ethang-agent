using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>Grand-plan timer fix, header half: the call card's right slot must
///     UPDATE live while the tool runs (the elapsed handle's PropertyChanged has to
///     reach the bound header TextBlock), and the exec budget must render as a
///     duration (900s as 15m), not raw seconds. Pins the split three-slot header:
///     live elapsed, separator, budget.</summary>
public class ToolHeaderTimeTests
{
  private const string ExecArgs = /*lang=json,strict*/ "{\"timeoutSeconds\":900,\"title\":\"t\",\"program\":\"return 1;\"}";

  [Fact]
  public void Budget_Formats_Whole_Minutes_As_Nm()
  {
    Assert.Equal("15m", ToolElapsed.FormatBudget(900));
    Assert.Equal("2m", ToolElapsed.FormatBudget(120));
    Assert.Equal("60m", ToolElapsed.FormatBudget(3600));
  }

  [Fact]
  public void Budget_Formats_SubMinute_As_Seconds_And_Odd_As_Composite()
  {
    Assert.Equal("45s", ToolElapsed.FormatBudget(45));
    Assert.Equal("2m 5s", ToolElapsed.FormatBudget(125));
  }

  [Fact]
  public void Exec_Call_HeaderTime_Composes_Live_Elapsed_And_Duration_Budget()
  {
    TranscriptViewModel vm = new();
    vm.AddToolCall("exec", ExecArgs);

    ToolCallEntry entry = Assert.IsType<ToolCallEntry>(Assert.Single(vm.Entries));
    Assert.Equal("0.0s / 15m", entry.HeaderTimeDisplay);
  }

  [Fact]
  public void Exec_Call_HeaderTime_Reflects_A_Ticked_Handle()
  {
    double now = 0;
    TranscriptViewModel vm = new(() => now);
    vm.AddToolCall("exec", ExecArgs);
    ToolCallEntry entry = Assert.IsType<ToolCallEntry>(vm.Entries[^1]);

    now = 1.2;
    vm.TickToolElapsed();

    Assert.Equal("1.2s / 15m", entry.HeaderTimeDisplay);
  }

  [AvaloniaFact]
  public void Exec_Call_Card_Header_TextBlock_Updates_When_The_Elapsed_Handle_Ticks()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    vm.Transcript.AddToolCall("exec", ExecArgs);
    Window window = new() { Content = new AgentView { DataContext = vm } };
    window.Show();
    Dispatcher.UIThread.RunJobs();

    ToolCallEntry entry = Assert.IsType<ToolCallEntry>(vm.Transcript.Entries[^1]);
    Expander card = TestFixtures.ToolCards((Control)window.Content).First();
    TextBlock elapsed = HeaderTextBlocks(card).First(t => t.Text == "0.0s");

    entry.Elapsed!.Display = "1.2s";
    Dispatcher.UIThread.RunJobs();

    Assert.Same(elapsed, HeaderTextBlocks(card).First(t => t.Text == "1.2s"));
    Assert.Contains("15m", HeaderTextBlocks(card).Select(t => t.Text));
  }

  [Fact]
  public void NonExec_Call_Shows_Live_Elapsed_And_No_Budget()
  {
    TranscriptViewModel vm = new();
    vm.AddToolCall("read", /*lang=json,strict*/ "{\"path\":\"a.cs\"}");

    ToolCallEntry entry = Assert.IsType<ToolCallEntry>(Assert.Single(vm.Entries));
    Assert.Equal("0.0s", entry.HeaderTimeDisplay);
    Assert.True(entry.HasLiveElapsed);
    Assert.Equal("", entry.BudgetDisplay);
  }

  [Fact]
  public void Restored_Exec_Call_Shows_Budget_Only_No_Live_Slot()
  {
    TranscriptViewModel vm = new();
    vm.Restore(
    [
      new Message(Role.Assistant, "",
          DateTimeOffset.UtcNow,
          [new ToolCall("c1", "exec", ExecArgs)]),
    ]);

    ToolCallEntry entry = Assert.IsType<ToolCallEntry>(Assert.Single(vm.Entries));
    Assert.False(entry.HasLiveElapsed);
    Assert.Equal("15m", entry.BudgetDisplay);
    Assert.Equal("15m", entry.HeaderTimeDisplay);
  }

  private static IEnumerable<TextBlock> HeaderTextBlocks(Expander card)
    => card.GetVisualDescendants().OfType<TextBlock>();

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
}
