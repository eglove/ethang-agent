using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;
using Xunit;

namespace eThangAgent.LiveModel.Tests;

/// <summary>TIER 1 of issue #289: the harness contract, asserted against a REAL free model
///     over the real provider wire.
///     <para>WHAT IS ASSERTED, AND WHY IT IS SAFE TO ASSERT: every assertion below is about
///     something THIS REPOSITORY controls - that the wire round-trips, that a tool result
///     reaches the model, that a typed error comes back as a result the model can read, that
///     the change report is emitted, that the transcript persists losslessly, that the ledger
///     records a price. None of them is about the model's judgement, so none of them is a coin
///     flip even though the model is picked at run time. A WEAK MODEL STILL PRODUCES A
///     WELL-FORMED TOOL CALL; if the harness loses it, that is a harness defect and this suite
///     is the only thing that can see it (every other suite's provider is written by the same
///     person who wrote the code under test).</para>
///     <para>ONE SESSION, SHARED BY THE SEQUENCE: the turns are deliberately cumulative
///     (<see cref="LiveModelSession"/>) because a free model queues and a real turn costs
///     wall-clock time; the sequence asserts the same facts in three turns instead of
///     opening a session per assertion.</para>
///     <para>EVERY FAILURE MESSAGE NAMES THE MODEL - the suite never pins one, so a red run
///     that did not say which model answered would be unfixable.</para></summary>
[Trait("Category", "LiveModel")]
public class LiveModelHarnessContractTests
{
  [Fact]
  public async Task LiveTurn_Settles_AndRecordsUsageWithNonZeroInputTokens()
  {
    LiveRunArtifacts run = await LiveModelSession.RunSequenceAsync().ConfigureAwait(true);

    // SETTLED: the turns completed and their artifacts were persisted. Persistence errors are
    // the lifecycle's own report - a non-empty list means the record is incomplete.
    Assert.True(run.PersistenceErrors.Count == 0,
        run.Fail("the live turn reported persistence errors: " + string.Join("; ", run.PersistenceErrors)));
    Assert.NotEmpty(run.AssistantText);

    // AT LEAST ONE PROVIDER CALL, WITH REAL TOKENS: a provider call that recorded no input
    // tokens did not happen, and a ledger that stops writing rows is invisible to every
    // fake-provider suite.
    Assert.True(run.Usage.Totals.Calls >= 1,
        run.Fail("the usage ledger recorded no provider call: " + LiveRun.Describe(run.Usage)));
    Assert.True(run.Usage.Totals.InputTokens > 0,
        run.Fail("the usage ledger recorded no input tokens: " + LiveRun.Describe(run.Usage)));

    // AND IT NAMES THE MODEL THAT SERVED IT (issue #290): a row whose model cannot be named
    // makes every measurement unattributable.
    Assert.Contains(run.Usage.ByModel, total => !string.IsNullOrEmpty(total.ModelId));
  }

  [Fact]
  public async Task LiveTurn_PersistsEveryMessageLosslessly_WithNoDanglingToolCallIds()
  {
    LiveRunArtifacts run = await LiveModelSession.RunSequenceAsync().ConfigureAwait(true);

    // THE ROUND TRIP: the transcript read back is the conversation's own length - nothing was
    // dropped on the way to disk, and nothing was doubled.
    Assert.True(run.PersistedTranscript.Count == run.ConversationLength,
        run.Fail($"the persisted transcript holds {run.PersistedTranscript.Count} messages, "
        + $"but the conversation held {run.ConversationLength}"));

    // NO DANGLING IDS: every tool call the assistant emitted has a tool result answering it.
    // A lost tool result is precisely the wire regression a fake provider cannot reveal.
    Assert.True(run.DanglingToolCallIds().Count == 0,
        run.Fail("tool calls with no persisted result: " + string.Join(", ", run.DanglingToolCallIds())));
  }

  [Fact]
  public async Task LiveTurn_TypedToolError_ReachesTheModelAsAToolResult()
  {
    LiveRunArtifacts run = await LiveModelSession.RunSequenceAsync().ConfigureAwait(true);

    // THE TYPED-ERROR CONTRACT: a deliberately invalid call comes back to the model as an
    // `Error [Code]:` result rather than ending the turn. This is the self-correction loop the
    // whole tool surface depends on, and it crosses the provider wire to prove itself.
    bool sawTypedError = run.ToolResultTexts().Any(text =>
        text.Contains("Error [", StringComparison.Ordinal));
    Assert.True(sawTypedError,
        run.Fail("no typed 'Error [Code]:' tool result was persisted; the loop continued without "
        + "ever seeing a refusal, so the assertion cannot be shown to hold"));
  }

  [Fact]
  public async Task LiveTurn_FileChangingExecRun_EmitsTheChangeReport()
  {
    LiveRunArtifacts run = await LiveModelSession.RunSequenceAsync().ConfigureAwait(true);

    // THE CHANGE REPORT (issue #283): an exec run that changed a file says so, as a FACT
    // about effects. The report rides the exec result, so it survives only if the whole
    // snapshot -> compare -> annotate -> persist chain works against a live tool call.
    Assert.Contains(run.ToolResultTexts(),
        text => text.Contains("[exec: modified ", StringComparison.Ordinal));
  }

  [Fact]
  public async Task LiveSession_Resumes_WithTheFullTranscriptHydrated()
  {
    LiveRunArtifacts run = await LiveModelSession.RunSequenceAsync().ConfigureAwait(true);

    // RESUME (the artifact's second life): a resumed session hydrates the SAME conversation,
    // which is what makes the transcript a resume source rather than a log.
    AgentSessionFactory factory = Assert.IsType<AgentSessionFactory>(LiveModelSession.Factory);
    Result<AgentSession> resumed = await factory.ResumeAsync(
        run.RootId, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(resumed.IsSuccess,
        run.Fail(resumed.IsSuccess ? string.Empty
            : $"the live session could not be resumed: {resumed.Error.Message}"));
    try
    {
      // HYDRATION IS A PREFIX PLUS THE RESUME NOTICE. Resume appends one System line reminding
      // the model that the CURRENT build's tool contracts outrank any contract text quoted
      // earlier in the transcript (the session may have outlived an app update), so the
      // hydrated conversation is the persisted transcript FOLLOWED BY that notice - never a
      // truncation, never a reordering.
      IReadOnlyList<Message> hydrated = resumed.Value.Conversation.Messages;
      Assert.True(hydrated.Count > run.PersistedTranscript.Count,
          run.Fail($"the resumed conversation holds {hydrated.Count} messages and did not "
              + $"carry the {run.PersistedTranscript.Count} persisted ones plus the resume notice"));
      Assert.True(hydrated.Take(run.PersistedTranscript.Count)
              .Select(message => (message.Role, message.Content))
              .SequenceEqual(run.PersistedTranscript.Select(message => (message.Role, message.Content))),
          run.Fail("the resumed conversation's prefix does not match the persisted transcript"));
    }
    finally
    {
      await resumed.Value.Services.DisposeAsync().ConfigureAwait(true);
      // LAST: no other test needs the persisted record after the resume has read it.
      await LiveModelSession.DeleteRecordAsync().ConfigureAwait(true);
    }
  }
}
