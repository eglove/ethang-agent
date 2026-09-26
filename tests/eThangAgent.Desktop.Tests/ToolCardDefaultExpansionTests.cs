using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Desktop.Markdown;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>Headless pin for the tool cards' default expansion: call and result
///     cards start expanded - a tool's arguments and output read without a click.
///     The user can still collapse any card; only the default changed.</summary>
public class ToolCardDefaultExpansionTests
{
  [AvaloniaFact]
  public void Tool_Call_And_Result_Cards_Start_Expanded()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    vm.Transcript.AddToolCall("read", /*lang=json,strict*/ "{\"path\":\"a.cs\"}");
    vm.Transcript.AddToolResult("read", "12 lines", "file body", false);
    Window window = new() { Width = 900, Height = 600, Content = new AgentView { DataContext = vm } };
    window.Show();
    Dispatcher.UIThread.RunJobs();
    AgentView view = (AgentView)window.Content;

    Expander[] cards = [.. TestFixtures.ToolCards(view)];
    Assert.Equal(2, cards.Length);
    Assert.All(cards, card => Assert.True(card.IsExpanded, "tool cards must start expanded"));

    // The result body is readable without any interaction.
    _ = view.GetVisualDescendants().OfType<SelectableTextBlock>()
        .First(b => b.IsVisible && b.Text is not null && b.Text.Contains("file body", StringComparison.Ordinal));
  }

  [AvaloniaFact]
  public void Exec_Call_Card_Starts_Expanded_Showing_The_Program()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    vm.Transcript.AddToolCall("exec", /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"title\":\"parse names\",\"program\":\"return 42;\"}");
    Window window = new() { Width = 900, Height = 600, Content = new AgentView { DataContext = vm } };
    window.Show();
    Dispatcher.UIThread.RunJobs();
    AgentView view = (AgentView)window.Content;

    Expander card = Assert.Single(TestFixtures.ToolCards(view));
    Assert.True(card.IsExpanded, "the exec call card must start expanded");

    // The program body renders through the markdown renderer without interaction.
    AgentMarkdownBlock body = view.GetVisualDescendants().OfType<AgentMarkdownBlock>()
        .Single(b => b.MarkdownText.Contains("return 42;", StringComparison.Ordinal));
    Assert.True(body.IsVisible, "the expanded exec card's program body must be visible");
  }
}
