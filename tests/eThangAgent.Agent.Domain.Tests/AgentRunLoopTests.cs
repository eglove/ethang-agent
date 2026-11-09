using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

public class AgentRunLoopTests
{
  private static readonly DateTimeOffset FixedNow = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

  private static AgentRecord Child(string model = "default/sub-model",
      string taskPrompt = "do things", string? label = null)
      => AgentRecord.Started(AgentId.NewId(), model, label, taskPrompt, FixedNow);

  private static AgentRunLoop MakeRunner(
      IModelProvider provider,
      FakeAgentStore store,
      AgentRunOptions? options = null,
      IToolRegistry? tools = null)
      => new(new AgentRunServices(
          new FakeModelProviderFactory(provider),
          store,
          tools ?? new ToolRegistry([]),
          new StaticPromptProvider("guide text"),
              options ?? new AgentRunOptions(DefaultModel: "default/sub-model")));

  [Fact]
  public async Task RunAsync_AStartedRecord_Completes_AndReportsItsStatus()
  {
    FakeAgentStore store = new();
    FakeProvider provider = new(
        Result.Success(new ModelResponse("deep report", [])));
    AgentRunLoop spawner = MakeRunner(provider, store,
        options: new AgentRunOptions(DefaultModel: "m/sub"));

    AgentRunOutcome outcome = await spawner.RunAsync(Child(model: "m/sub"), CancellationToken.None);

    Assert.Equal(AgentStatus.Completed, outcome.Status);
  }

  // --- model plumbing ---

  [Fact]
  public async Task RunAsync_CreatesProvider_FromTheChildRecordsModel()
  {
    FakeModelProviderFactory factory = new(new FakeProvider());
    FakeAgentStore store = new();
    AgentRunLoop spawner = new(new AgentRunServices(factory, store, new ToolRegistry([]),
            new StaticPromptProvider("guide text"), new AgentRunOptions(DefaultModel: "unused/default")));

    _ = await spawner.RunAsync(Child(model: "explicit/model"), CancellationToken.None);

    Assert.Equal(1, factory.CreateCount);
    Assert.Equal("explicit/model", factory.LastConfig!.ModelId);
  }

  // --- persistence flow ---

  [Fact]
  public async Task RunAsync_PersistsCompleted_WithReportAndIdentity()
  {
    FakeAgentStore store = new();
    FakeProvider provider = new(
        Result.Success(new ModelResponse("child report", [])));
    AgentRunLoop spawner = MakeRunner(provider, store);

    AgentRecord child = Child(taskPrompt: "summarize the file", label: "child-a");
    AgentRunOutcome outcome = await spawner.RunAsync(child, CancellationToken.None);

    // RunAsync never saves the Running row — the spawn command owns that write.
    Assert.Empty(store.Saved);

    AgentRecord updated = Assert.Single(store.Updated);
    Assert.Equal(child.Id, updated.Id);
    Assert.Equal(AgentStatus.Completed, updated.Status);
    Assert.Null(updated.FailureReason);
    _ = Assert.NotNull(updated.CompletedAt);
    Assert.Equal("child report", updated.FinalReport);

    Assert.Equal(child.Id, outcome.AgentId);
    Assert.Equal(AgentStatus.Completed, outcome.Status);
    Assert.Null(outcome.Reason);
    Assert.Equal("child report", outcome.Report);
    Assert.Equal("default/sub-model", outcome.ModelUsed);
  }

  [Fact]
  public async Task RunAsync_Completed_AppendsChildTranscript()
  {
    FakeAgentStore store = new();
    FakeProvider provider = new(
        Result.Success(new ModelResponse("child report", [])));
    AgentRunLoop spawner = MakeRunner(provider, store);

    AgentRunOutcome outcome = await spawner.RunAsync(Child(taskPrompt: "do things"), CancellationToken.None);

    Result<IReadOnlyList<Message>> transcript = await store.GetTranscriptAsync(outcome.AgentId, ct: TestContext.Current.CancellationToken);

    Assert.True(transcript.IsSuccess);
    Assert.Equal(2, transcript.Value.Count);
    Assert.Equal(Role.User, transcript.Value[0].Role);
    Assert.Equal("do things", transcript.Value[0].Content);
    Assert.Equal(Role.Assistant, transcript.Value[1].Role);
    Assert.Equal("child report", transcript.Value[1].Content);
  }

  [Fact]
  public async Task RunAsync_SeedsChildConversation_AndSuppliesSystemPromptAndTools()
  {
    FakeTool fakeTool = new("read_file", "file content");
    FakeAgentStore store = new();
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null,
            [new ToolCallRequest("call_1", "read_file", "{}")])),
        Result.Success(new ModelResponse("finished", [])));
    AgentRunLoop spawner = MakeRunner(provider, store,
        tools: new ToolRegistry([fakeTool]));

    _ = await spawner.RunAsync(Child(taskPrompt: "read it"), CancellationToken.None);

    ModelRequest request = provider.RequestsSeen[0]; // first of two turns: tool call, then final report
    Assert.Equal("guide text", request.SystemPrompt);
    Assert.NotNull(request.Tools);
    Assert.Contains(request.Tools, t => t.Name == "read_file");
    Assert.Equal(Role.User, request.Messages[0].Role);
    Assert.Equal("read it", request.Messages[0].Content);

    // The tool result was appended after request 1 was sent, so it lives in the child's
    // conversation — requests are frozen snapshots, not live views of the growing list.
    AgentId outcomeChildId = store.Updated.Single().Id;
    Result<IReadOnlyList<Message>> transcript = await store.GetTranscriptAsync(outcomeChildId, ct: TestContext.Current.CancellationToken);
    Assert.True(transcript.IsSuccess);
    Assert.Equal(4, transcript.Value.Count);
    Assert.Equal("file content", transcript.Value[2].Content); // tool result fed back
  }

  // --- failure paths ---

  [Fact]
  public async Task RunAsync_UserInterruption_RecordsFailedInterrupted_DistinctFromTimeout()
  {
    FakeAgentStore store = new();
    AgentRunLoop spawner = MakeRunner(new BlockingProvider(), store);
    using CancellationTokenSource cts = new();
    await cts.CancelAsync(); // the runtime interrupts by cancelling this token

    AgentRunOutcome outcome = await spawner.RunAsync(Child(), cts.Token);

    Assert.Equal(AgentStatus.Failed, outcome.Status);
    Assert.Equal(AgentFailureReason.Interrupted, outcome.Reason);
    AgentRecord updated = Assert.Single(store.Updated);
    Assert.Equal(AgentFailureReason.Interrupted, updated.FailureReason);
  }

  [Fact]
  public async Task RunAsync_Interrupted_ReturnsFailedOutcome_PersistsInterrupted()
  {
    FakeAgentStore store = new();
    AgentRunLoop spawner = MakeRunner(new BlockingProvider(), store);
    using CancellationTokenSource cts = new();

    Task<AgentRunOutcome> run = spawner.RunAsync(Child(), cts.Token);
    await cts.CancelAsync().ConfigureAwait(true);
    AgentRunOutcome outcome = await run.ConfigureAwait(true);

    Assert.Equal(AgentStatus.Failed, outcome.Status);
    Assert.Equal(AgentFailureReason.Interrupted, outcome.Reason);

    AgentRecord updated = Assert.Single(store.Updated);
    Assert.Equal(AgentStatus.Failed, updated.Status);
    Assert.Equal(AgentFailureReason.Interrupted, updated.FailureReason);
    _ = Assert.NotNull(updated.CompletedAt);
  }
  [Fact]
  public async Task RunAsync_ProviderThrows_ReturnsFailedOutcome_PersistsProviderError()
  {
    FakeAgentStore store = new();
    AgentRunLoop spawner = MakeRunner(new ThrowingProvider(), store);

    AgentRunOutcome outcome = await spawner.RunAsync(Child(), CancellationToken.None);

    Assert.Equal(AgentStatus.Failed, outcome.Status);
    Assert.Equal(AgentFailureReason.ProviderError, outcome.Reason);

    AgentRecord updated = Assert.Single(store.Updated);
    Assert.Equal(AgentStatus.Failed, updated.Status);
    Assert.Equal(AgentFailureReason.ProviderError, updated.FailureReason);
  }

  // There is no wall-clock budget anymore (FR-L4/A4): a looping child runs until the
  // watchdog's graduated policy or the user's stop interrupts it. This test pins the
  // interrupt path against a looping provider.
  [Fact]
  public async Task RunAsync_LoopingChild_Interrupts_Cleanly()
  {
    FakeAgentStore store = new();
    GatedTool tool = new("loop");
    AgentRunLoop spawner = MakeRunner(new LoopingProvider(), store,
        options: new AgentRunOptions(DefaultModel: "default/sub-model"),
        tools: new ToolRegistry([tool]));
    using CancellationTokenSource cts = new();

    // The run parks inside the tool, so the cancel reaches a run that is provably mid-loop
    // rather than one the clock happened to catch.
    Task<AgentRunOutcome> running = spawner.RunAsync(Child(taskPrompt: "loop forever"), cts.Token);
    await tool.Entered.Task.WaitAsync(TestCeiling.Value, TestContext.Current.CancellationToken).ConfigureAwait(true);
    await cts.CancelAsync().ConfigureAwait(true);
    _ = tool.Release.TrySetResult();
    AgentRunOutcome outcome = await running.WaitAsync(TestCeiling.Value,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal(AgentStatus.Failed, outcome.Status);
    Assert.Equal(AgentFailureReason.Interrupted, outcome.Reason);

    AgentRecord updated = Assert.Single(store.Updated);
    Assert.Equal(AgentStatus.Failed, updated.Status);
    Assert.Equal(AgentFailureReason.Interrupted, updated.FailureReason);
  }

  // --- terminal persistence fault ---

  [Fact]
  public async Task RunAsync_TerminalUpdateFails_ThrowsInfrastructureError()
  {
    FakeAgentStore store = new() { UpdateFailure = Result.Failure<string>(new DomainError("StorageDown", "agent store unavailable.")) };
    FakeProvider provider = new(
        Result.Success(new ModelResponse("report", [])));
    AgentRunLoop spawner = MakeRunner(provider, store);

    _ = await Assert.ThrowsAsync<InvalidOperationException>(
        () => spawner.RunAsync(Child(), CancellationToken.None));
  }

  [Fact]
  public async Task RunAsync_OversizedReport_PersistedAndReportedInFull()
  {
    string bigReport = new('x', 52_000);
    FakeAgentStore store = new();
    FakeProvider provider = new(
        Result.Success(new ModelResponse(bigReport, [])));
    AgentRunLoop spawner = MakeRunner(provider, store);

    AgentRunOutcome outcome = await spawner.RunAsync(Child(taskPrompt: "big task"), CancellationToken.None);

    Assert.Equal(AgentStatus.Completed, outcome.Status);
    Assert.Equal(bigReport, outcome.Report);

    AgentRecord updated = Assert.Single(store.Updated);
    Assert.Equal(bigReport, updated.FinalReport);
  }
}
