using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.Streaming;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>Status-line parser contract for the root model selection notices
///     (spec #55 section 2). The resolver and the measured-mix reselection coordinator
///     both emit 'Model selected: &lt;id&gt; via &lt;provider&gt;' when a provider is known,
///     'Model selected: &lt;id&gt;' when it is not, and the fallback form
///     'Model selection failed: ...; using &lt;id&gt;.' The status bar tracks the model id
///     through all three; the ' via &lt;provider&gt;' suffix must never leak into the id.</summary>
public class SelectionNoticeParserTests
{
  [Theory]
  [InlineData("Model selected: m/x via B", "m/x")]
  [InlineData("Model selected: m/x", "m/x")]
  [InlineData("Model selection failed: boom; using openrouter/auto.", "openrouter/auto")]
  public async Task Notice_Updates_Status_Model_Id(string notice, string expected)
  {
    string? captured = await ParseAsync(notice).ConfigureAwait(true);

    Assert.Equal(expected, captured);
  }

  private static async Task<string?> ParseAsync(string notice)
  {
    string? captured = null;
    AgentSessionViewModel vm = new(
        static (_, _, _, _) => Task.FromResult(Result.Success("ok")),
        new RecordingLifecycle(new TestFixtures.StubStore()),
        AgentId.NewId(),
        new Conversation(),
        "OpenRouter",
        "test/model",
        new AgentSessionViewModelOptions
        {
          WorkspaceRoot = @"C:\work\demo",
          StatusModelUpdater = id => captured = id,
        });

    await vm.ApplyUiStreamEventAsync(new UiStreamEvent.Notice(notice)).ConfigureAwait(true);
    return captured;
  }
}
