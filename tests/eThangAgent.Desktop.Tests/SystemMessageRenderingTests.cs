using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>Headless pin for the system-message DataTemplate: without a template the
///     entry type renders NOTHING in the transcript (the empty-slot failure mode), so
///     the class contract alone is not enough. Pins that a live system message becomes
///     visible text in the rendered visual tree.</summary>
public class SystemMessageRenderingTests
{
  [AvaloniaFact]
  public void SystemMessageEntry_RendersVisibleText_InTheTranscript()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    vm.Transcript.AddSystemMessage("[nudge] remember to curate");

    Window window = new() { Content = new AgentView { DataContext = vm } };
    window.Show();
    Dispatcher.UIThread.RunJobs();

    AgentView view = (AgentView)window.Content;
    SelectableTextBlock rendered = view.GetVisualDescendants().OfType<SelectableTextBlock>()
        .First(b => b.Text is not null && b.Text.Contains("[nudge] remember to curate", StringComparison.Ordinal));

    Assert.Contains("[nudge] remember to curate", rendered.Text, StringComparison.Ordinal);
  }

  [AvaloniaFact]
  public void SystemMessageEntry_IsDistinctFromNoticeEntries()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    vm.Transcript.AddNotice("host notice");
    vm.Transcript.AddSystemMessage("[nudge] loop voice");

    Window window = new() { Content = new AgentView { DataContext = vm } };
    window.Show();
    Dispatcher.UIThread.RunJobs();

    AgentView view = (AgentView)window.Content;
    // The system message renders through its own template: a ❖ glyph marker in a
    // dedicated column, with the message text selectable beside it - visibly a
    // different kind than the plain dark-gray notice line.
    TextBlock glyph = view.GetVisualDescendants().OfType<TextBlock>()
        .First(b => b.Text is not null && b.Text.Contains('❖', StringComparison.Ordinal));
    SelectableTextBlock message = view.GetVisualDescendants().OfType<SelectableTextBlock>()
        .First(b => b.Text is not null && b.Text.Contains("[nudge] loop voice", StringComparison.Ordinal));
    SelectableTextBlock notice = view.GetVisualDescendants().OfType<SelectableTextBlock>()
        .First(b => b.Text is not null && b.Text.Contains("host notice", StringComparison.Ordinal));

    Assert.NotNull(glyph);
    Assert.NotNull(message);
    Assert.NotNull(notice);
  }
}
