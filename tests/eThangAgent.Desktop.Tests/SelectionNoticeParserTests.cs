using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.Streaming;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>Status-line parser contract for the root resolver's selection notice.
///     The ONLY live notice contract is RootAgentResolver's preferred-pick line,
///     "Model selected: &lt;id&gt;[ via &lt;provider&gt;]", and the notice must START with
///     that prefix. Transient notices - tool errors, repeat-guard lines, host
///     health - ride the same event and must never reach the status bar's model
///     cell (issue #247: the removed fallback contract's loose "using " match
///     parsed arbitrary notice prose as a model id and clobbered the model name).</summary>
public class SelectionNoticeParserTests
{
  [Theory]
  [InlineData("Model selected: m/x via B", "m/x")]
  [InlineData("Model selected: m/x", "m/x")]
  public async Task Notice_Updates_Status_Model_Id(string notice, string expected)
  {
    string? captured = await ParseAsync(notice).ConfigureAwait(true);

    Assert.Equal(expected, captured);
  }

  [Fact]
  public async Task Repeat_Guard_Notice_Never_Clobbers_The_Model_Cell()
  {
    // The repeat-guard line carries "using ... ." prose. The removed fallback
    // contract parsed it as a model id and replaced the model name in the status
    // bar (issue #247); the parser must leave the status bar untouched.
    string? captured = await ParseAsync(
        "[repeat guard] Tool 'exec' has now failed 3 consecutive calls (any arguments). "
        + "Stop using this tool for now: fix the arguments, re-read the tool's usage "
        + "documentation, or take a different approach entirely.").ConfigureAwait(true);

    Assert.Null(captured);
  }

  [Fact]
  public async Task Error_Notice_With_A_Path_Containing_Using_Never_Clobbers_The_Model_Cell()
  {
    // Tool-error text is arbitrary; any "using" plus a period inside it must not
    // read as a model id.
    string? captured = await ParseAsync(
        @"Error [ImageReadFailed]: could not read 'C:\work\file using config.json': blocked.")
        .ConfigureAwait(true);

    Assert.Null(captured);
  }

  [Fact]
  public async Task Notice_Mentioning_Selection_MidText_Does_Not_Parse()
  {
    // Strictness: the notice must START with the contract prefix, so a transient
    // message that merely quotes it cannot move the status bar.
    string? captured = await ParseAsync("earlier the resolver said Model selected: m/x")
        .ConfigureAwait(true);

    Assert.Null(captured);
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
