using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Regression tests for the 2026-10-04 incident: an interrupt destroyed a
///     31-minute child's transcript (terminal persistence ran under the cancelled run
///     token, threw, and the runtime catch-all mislabeled the death ProviderError
///     'A task was canceled.'). The contract now: the transcript survives an interrupt
///     up to the last safe point, and a persistence fault is reported honestly.</summary>
public class AgentRunLoopInterruptPersistenceTests
{
  private static readonly DateTimeOffset FixedNow = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

  private static AgentRecord Child(string taskPrompt = "do things")
      => AgentRecord.Started(AgentId.NewId(), "default/sub-model", null, taskPrompt, FixedNow);

  private static AgentRunLoop MakeRunner(IModelProvider provider, IAgentStore store, IToolRegistry? tools = null)
      => new(new AgentRunServices(
          new FakeModelProviderFactory(provider), store, tools ?? new ToolRegistry([]),
          new StaticPromptProvider("guide text"), new AgentRunOptions(DefaultModel: "default/sub-model")));

  [Fact]
  public async Task RunAsync_InterruptedMidLoop_TranscriptSurvives()
  {
    FakeAgentStore store = new();
    GatedTool tool = new("loop");
    AgentRunLoop spawner = MakeRunner(new LoopingProvider(), store,
        tools: new ToolRegistry([tool]));
    using CancellationTokenSource cts = new();

    // The interrupt lands on a FACT the test controls: the run is inside the tool, and the
    // cancel is issued only once it is there. No duration decides when the run is interrupted.
    Task<AgentRunOutcome> running = spawner.RunAsync(Child(taskPrompt: "loop forever"), cts.Token);
    await tool.Entered.Task.WaitAsync(TestCeiling.Value, TestContext.Current.CancellationToken).ConfigureAwait(true);
    await cts.CancelAsync().ConfigureAwait(true);
    _ = tool.Release.TrySetResult();
    AgentRunOutcome outcome = await running.WaitAsync(TestCeiling.Value,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal(AgentStatus.Failed, outcome.Status);
    Assert.Equal(AgentFailureReason.Interrupted, outcome.Reason);
    Result<IReadOnlyList<Message>> transcript = await store.GetTranscriptAsync(outcome.AgentId, ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(transcript.IsSuccess);
    // The task prompt plus every loop-iterated message pair persisted incrementally,
    // not just the terminal row: the run's history survives the interrupt.
    Assert.True(transcript.Value.Count >= 3,
        $"expected the interrupted run's transcript to survive; got {transcript.Value.Count} rows");
  }

  [Fact]
  public async Task RunAsync_Completed_TranscriptPersistedIncrementallyDuringRun()
  {
    FakeAgentStore store = new();
    GatedTool tool = new("loop");
    AgentRunLoop spawner = MakeRunner(new LoopingProvider(), store,
        tools: new ToolRegistry([tool]));
    using CancellationTokenSource cts = new();

    Task<AgentRunOutcome> running = spawner.RunAsync(Child(taskPrompt: "loop forever"), cts.Token);
    await tool.Entered.Task.WaitAsync(TestCeiling.Value, TestContext.Current.CancellationToken).ConfigureAwait(true);
    await cts.CancelAsync().ConfigureAwait(true);
    _ = tool.Release.TrySetResult();
    _ = await running.WaitAsync(TestCeiling.Value, TestContext.Current.CancellationToken).ConfigureAwait(true);

    // Incremental: appends happened DURING the run, one per conversation mutation,
    // interleaved with loop progress — not one bulk write at terminal.
    Assert.True(store.AppendedMessages.Count >= 3,
        $"expected incremental appends during the run; got {store.AppendedMessages.Count}");
  }
}
