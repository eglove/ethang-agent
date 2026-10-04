using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Regression tests for the 2026-10-04 incident: an interrupt destroyed a
///     31-minute child's transcript (terminal persistence ran under the cancelled run
///     token, threw, and the runtime catch-all mislabeled the death ProviderError
///     'A task was canceled.'). The contract now: the transcript survives an interrupt
///     up to the last safe point, and a persistence fault is reported honestly.</summary>
public class SubAgentSpawnerInterruptPersistenceTests
{
  private static readonly DateTimeOffset FixedNow = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

  private static AgentRecord Child(string taskPrompt = "do things")
      => AgentRecord.Spawned(AgentId.NewId(), null, 1, "default/sub-model", null, taskPrompt, FixedNow);

  private static SubAgentSpawner MakeRunner(IModelProvider provider, IAgentStore store, IToolRegistry? tools = null)
      => new(new SubAgentServices(
          new FakeModelProviderFactory(provider), store, tools ?? new ToolRegistry([]),
          new StaticPromptProvider("guide text"), new SubAgentOptions(DefaultModel: "default/sub-model")));

  [Fact]
  public async Task RunAsync_InterruptedMidLoop_TranscriptSurvives()
  {
    FakeAgentStore store = new();
    SubAgentSpawner spawner = MakeRunner(new LoopingProvider(), store,
        tools: new ToolRegistry([new FakeTool("loop", "again")]));
    using CancellationTokenSource cts = new();
    cts.CancelAfter(TimeSpan.FromMilliseconds(200));

    AgentRunOutcome outcome = await spawner.RunAsync(Child(taskPrompt: "loop forever"), cts.Token).ConfigureAwait(true);

    Assert.Equal(AgentStatus.Failed, outcome.Status);
    Assert.Equal(AgentFailureReason.Interrupted, outcome.Reason);
    Result<IReadOnlyList<Message>> transcript = await store.GetTranscriptAsync(outcome.ChildId, ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
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
    SubAgentSpawner spawner = MakeRunner(new LoopingProvider(), store,
        tools: new ToolRegistry([new FakeTool("loop", "again")]));
    using CancellationTokenSource cts = new();
    cts.CancelAfter(TimeSpan.FromMilliseconds(300));

    _ = await spawner.RunAsync(Child(taskPrompt: "loop forever"), cts.Token).ConfigureAwait(true);

    // Incremental: appends happened DURING the run, one per conversation mutation,
    // interleaved with loop progress — not one bulk write at terminal.
    Assert.True(store.AppendedMessages.Count >= 3,
        $"expected incremental appends during the run; got {store.AppendedMessages.Count}");
  }
}
