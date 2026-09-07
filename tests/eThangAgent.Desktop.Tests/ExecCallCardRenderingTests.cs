using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Desktop.Markdown;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>Headless pin for the exec CALL card template: header shows [gear][title]
///     with elapsed/budget in the right slot, and the expanded body renders ONLY the
///     program through the markdown renderer - the raw JSON arguments never appear.</summary>
public class ExecCallCardRenderingTests
{
  [AvaloniaFact]
  public void ExecCallCard_ShowsTitleHeader_AndProgramBody_NeverTheJson()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    vm.Transcript.AddToolCall("exec", /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"title\":\"parse names\",\"program\":\"return 42;\"}");

    Window window = new() { Width = 900, Height = 600, Content = new AgentView { DataContext = vm } };
    window.Show();
    Dispatcher.UIThread.RunJobs();
    AgentView view = (AgentView)window.Content;
    Expander card = view.GetVisualDescendants().OfType<Expander>().First(e => e.Classes.Contains("tool-card"));
    card.IsExpanded = true;
    Dispatcher.UIThread.RunJobs();

    // Header: the title rides a Run inline; the args preview must NOT be there.
    TextBlock header = view.GetVisualDescendants().OfType<TextBlock>()
        .First(b => b.Inlines?.OfType<Avalonia.Controls.Documents.Run>()
            .Any(r => r.Text is not null && r.Text.Contains("parse names", StringComparison.Ordinal)) == true);
    Assert.NotNull(header);
    Assert.DoesNotContain("timeoutSeconds", header.Text ?? "", StringComparison.Ordinal);

    // Body: the fenced program via the markdown renderer; no JSON anywhere.
    _ = Assert.Single(view.GetVisualDescendants().OfType<AgentMarkdownBlock>(),
        b => b.MarkdownText.Contains("```csharp", StringComparison.Ordinal)
            && b.MarkdownText.Contains("return 42;", StringComparison.Ordinal));
    Assert.DoesNotContain(view.GetVisualDescendants().OfType<SelectableTextBlock>().Where(b => b.IsVisible),
        b => b.Text is not null && b.Text.Contains("timeoutSeconds", StringComparison.Ordinal));

    // Right slot: budget visible in the header time display.
    TextBlock time = view.GetVisualDescendants().OfType<TextBlock>()
        .First(b => b.Text is not null && b.Text.Contains("2m", StringComparison.Ordinal));
    Assert.NotNull(time);
  }
}
