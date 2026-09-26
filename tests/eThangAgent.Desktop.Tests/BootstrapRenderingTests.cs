using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.Markdown;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.SharedKernel;
using MarkView.Avalonia;

namespace eThangAgent.Desktop.Tests;

/// <summary>Headless pin for the bootstrap context entry's expanded body: the
///     verbatim system prompt renders through the markdown renderer (headings,
///     emphasis, code), not as a flat mono text block - the prompt is markdown
///     and must read as such.</summary>
public class BootstrapRenderingTests
{
  private const string Prompt = "# Session contract\n\n**Prefer** a matching skill over improvising.";

  [AvaloniaFact]
  public void Expanded_Bootstrap_Body_Renders_The_System_Prompt_As_Markdown()
  {
    AgentSessionViewModel vm = Build(Prompt);
    Window window = new() { Width = 900, Height = 600, Content = new AgentView { DataContext = vm } };
    window.Show();
    Dispatcher.UIThread.RunJobs();
    AgentView view = (AgentView)window.Content;

    // The bootstrap entry's expander: the transcript's only non-tool-card one.
    Expander bootstrap = view.GetVisualDescendants().OfType<Expander>()
        .Single(e => !e.Classes.Contains("tool-card"));
    bootstrap.IsExpanded = true;
    Dispatcher.UIThread.RunJobs();

    // The body goes through the markdown renderer with the verbatim prompt.
    AgentMarkdownBlock block = view.GetVisualDescendants().OfType<AgentMarkdownBlock>()
        .Single(b => b.MarkdownText == Prompt);
    _ = Assert.IsType<MarkdownViewer>(block.Content);

    // The old flat mono body is gone: no visible selectable text shows the prompt.
    Assert.DoesNotContain(view.GetVisualDescendants().OfType<SelectableTextBlock>()
            .Where(b => b.IsVisible),
        b => b.Text is not null && b.Text.Contains("Session contract", StringComparison.Ordinal));
  }

  private static AgentSessionViewModel Build(string systemPrompt)
  {
    return new AgentSessionViewModel(
        NoopRunner,
        new RootSessionLifecycle(new TestFixtures.StubStore()),
        AgentId.NewId(),
        new Conversation(),
        provider: "OpenRouter",
        modelId: "test/model",
        new AgentSessionViewModelOptions { WorkspaceRoot = @"C:\work\demo", SystemPrompt = systemPrompt });

#pragma warning disable IDE0060, S1172 // Delegate-shape parameters are unused by design.
    static Task<Result<string>> NoopRunner(
        SendMessageCommand _command, CancellationToken _ct, TurnCallbacks? _callbacks, Action<string>? _onNotice)
#pragma warning restore IDE0060, S1172
      => Task.FromResult(Result.Success("unused"));
  }
}
