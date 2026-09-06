using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Desktop.Markdown;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>Grand-plan tool-output item, headless pin: a rich exec result card shows
///     the title in its header and its fenced program body (through the markdown
///     renderer); a legacy card (no rich metadata) keeps the plain header and the full
///     content body. Bodies realize on expand, so the tests expand the card first.</summary>
public class RichToolResultRenderingTests
{
  [AvaloniaFact]
  public void RichResult_ShowsTitleInHeader_AndOutputInBody_NeverTheProgramAgain()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    vm.Transcript.AddToolResult("exec", "ok", "hello stdout\nline two", false, "parse names");

    Window window = new() { Width = 900, Height = 600, Content = new AgentView { DataContext = vm } };
    window.Show();
    Dispatcher.UIThread.RunJobs();
    AgentView view = (AgentView)window.Content;
    Expander card = view.GetVisualDescendants().OfType<Expander>().First(e => e.Classes.Contains("tool-card"));
    card.IsExpanded = true;
    Dispatcher.UIThread.RunJobs();

    // Header: the title rides a Run inline beside the tool name.
    TextBlock header = view.GetVisualDescendants().OfType<TextBlock>()
        .First(b => b.Inlines?.OfType<Avalonia.Controls.Documents.Run>()
            .Any(r => r.Text is not null && r.Text.Contains("parse names", StringComparison.Ordinal)) == true);
    Assert.NotNull(header);

    // Body: the program's OUTPUT in the mono body - the program/fence never re-renders.
    SelectableTextBlock body = view.GetVisualDescendants().OfType<SelectableTextBlock>()
        .First(b => b.IsVisible && b.Text is not null && b.Text.Contains("hello stdout", StringComparison.Ordinal));
    Assert.NotNull(body);
    Assert.DoesNotContain(view.GetVisualDescendants().OfType<AgentMarkdownBlock>()
        .Where(b => b.IsVisible),
        b => b.MarkdownText.Contains("```csharp", StringComparison.Ordinal));
  }

  [AvaloniaFact]
  public void LegacyResult_KeepsPlainHeaderAndFullContentBody()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    vm.Transcript.AddToolResult("read", "12 lines", "file body", false);

    Window window = new() { Width = 900, Height = 600, Content = new AgentView { DataContext = vm } };
    window.Show();
    Dispatcher.UIThread.RunJobs();
    AgentView view = (AgentView)window.Content;
    ExpandSingleCard(view);

    SelectableTextBlock body = view.GetVisualDescendants().OfType<SelectableTextBlock>()
        .First(b => b.Text is not null && b.Text.Contains("file body", StringComparison.Ordinal));
    Assert.NotNull(body);
  }

  private static void ExpandSingleCard(AgentView view)
  {
    Expander card = view.GetVisualDescendants().OfType<Expander>().First(e => e.Classes.Contains("tool-card"));
    card.IsExpanded = true;
    Dispatcher.UIThread.RunJobs();
  }
}
