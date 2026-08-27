using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

public class SubAgentSpawnerTests
{
  private static readonly DateTimeOffset FixedNow = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

  private static AgentRecord Child(int depth = 0, string model = "default/sub-model",
      string taskPrompt = "do things", string? label = null)
      => AgentRecord.Spawned(AgentId.NewId(), null, depth, model, label, taskPrompt, FixedNow);

  private static SubAgentSpawner MakeRunner(
      IModelProvider provider,
      FakeAgentStore store,
      SubAgentOptions? options = null,
      IToolRegistry? tools = null)
      => new(
          new FakeModelProviderFactory(provider),
          store,
          tools ?? new ToolRegistry([]),
          new StaticPromptProvider("guide text"),
          options ?? new SubAgentOptions(DefaultModel: "default/sub-model"));

  // --- depth passes through (the guard itself is covered by StartSpawnHandlerTests) ---

  [Fact]
  public async Task RunAsync_ChildAtDepthBoundary_Completes_OutcomeCarriesChildsDepth()
  {
    FakeAgentStore store = new();
    FakeProvider provider = new(
        Result.Success(new ModelResponse("deep report", [])));
    SubAgentSpawner spawner = MakeRunner(provider, store,
        options: new SubAgentOptions(DefaultModel: "m/sub", MaxDepth: 3));

    AgentRunOutcome outcome = await spawner.RunAsync(Child(depth: 3, model: "m/sub"), CancellationToken.None);

    Assert.Equal(AgentStatus.Completed, outcome.Status);
    Assert.Equal(3, outcome.Depth);
  }

  // --- model plumbing (resolution precedence is covered by StartSpawnHandlerTests) ---

  [Fact]
  public async Task RunAsync_CreatesProvider_FromTheChildRecordsModel()
  {
    FakeModelProviderFactory factory = new(new FakeProvider());
    FakeAgentStore store = new();
    SubAgentSpawner spawner = new(factory, store, new ToolRegistry([]),
        new StaticPromptProvider("guide text"), new SubAgentOptions(DefaultModel: "unused/default"));

    _ = await spawner.RunAsync(Child(depth: 0, model: "explicit/model"), CancellationToken.None);

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
    SubAgentSpawner spawner = MakeRunner(provider, store);

    AgentRecord child = Child(depth: 2, taskPrompt: "summarize the file", label: "child-a");
    AgentRunOutcome outcome = await spawner.RunAsync(child, CancellationToken.None);

    // RunAsync never saves the Running row — the spawn command owns that write.
    Assert.Empty(store.Saved);

    AgentRecord updated = Assert.Single(store.Updated);
    Assert.Equal(child.Id, updated.Id);
    Assert.Equal(AgentStatus.Completed, updated.Status);
    Assert.Null(updated.FailureReason);
    _ = Assert.NotNull(updated.CompletedAt);
    Assert.Equal("child report", updated.FinalReport);

    Assert.Equal(child.Id, outcome.ChildId);
    Assert.Equal(AgentStatus.Completed, outcome.Status);
    Assert.Null(outcome.Reason);
    Assert.Equal("child report", outcome.Report);
    Assert.Equal("default/sub-model", outcome.ModelUsed);
    Assert.Equal(2, outcome.Depth);
  }

  [Fact]
  public async Task RunAsync_Completed_AppendsChildTranscript()
  {
    FakeAgentStore store = new();
    FakeProvider provider = new(
        Result.Success(new ModelResponse("child report", [])));
    SubAgentSpawner spawner = MakeRunner(provider, store);

    AgentRunOutcome outcome = await spawner.RunAsync(Child(taskPrompt: "do things"), CancellationToken.None);

    Result<IReadOnlyList<Message>> transcript = await store.GetTranscriptAsync(outcome.ChildId);

    Assert.True(transcript.IsSuccess);
    Assert.Equal(2, transcript.Value!.Count);
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
    SubAgentSpawner spawner = MakeRunner(provider, store,
        tools: new ToolRegistry([fakeTool]));

    _ = await spawner.RunAsync(Child(taskPrompt: "read it"), CancellationToken.None);

    ModelRequest request = provider.RequestsSeen[0]; // first of two turns: tool call, then final report
    Assert.Equal("guide text", request.SystemPrompt);
    Assert.NotNull(request.Tools);
    Assert.Contains(request.Tools!, t => t.Name == "read_file");
    Assert.Equal(Role.User, request.Messages[0].Role);
    Assert.Equal("read it", request.Messages[0].Content);

    // The tool result was appended after request 1 was sent, so it lives in the child's
    // conversation — requests are frozen snapshots, not live views of the growing list.
    AgentId outcomeChildId = store.Updated.Single().Id;
    Result<IReadOnlyList<Message>> transcript = await store.GetTranscriptAsync(outcomeChildId);
    Assert.True(transcript.IsSuccess);
    Assert.Equal(4, transcript.Value!.Count);
    Assert.Equal("file content", transcript.Value[2].Content); // tool result fed back
  }

  // --- failure paths ---

  [Fact]
  public async Task RunAsync_UserInterruption_RecordsFailedInterrupted_DistinctFromTimeout()
  {
    FakeAgentStore store = new();
    SubAgentSpawner spawner = MakeRunner(new BlockingProvider(), store);
    using CancellationTokenSource cts = new();
    await cts.CancelAsync(); // the runtime interrupts by cancelling this token

    AgentRunOutcome outcome = await spawner.RunAsync(Child(), cts.Token);

    Assert.Equal(AgentStatus.Failed, outcome.Status);
    Assert.Equal(AgentFailureReason.Interrupted, outcome.Reason);
    AgentRecord updated = Assert.Single(store.Updated);
    Assert.Equal(AgentFailureReason.Interrupted, updated.FailureReason);
  }

  [Fact]
  public async Task RunAsync_Timeout_ReturnsFailedOutcome_PersistsFailedTimeout()
  {
    FakeAgentStore store = new();
    SubAgentSpawner spawner = MakeRunner(new BlockingProvider(), store,
        options: new SubAgentOptions(DefaultModel: "m/sub",
            ChildTimeout: TimeSpan.FromMilliseconds(50)));

    AgentRunOutcome outcome = await spawner.RunAsync(Child(), CancellationToken.None);

    Assert.Equal(AgentStatus.Failed, outcome.Status);
    Assert.Equal(AgentFailureReason.Timeout, outcome.Reason);

    AgentRecord updated = Assert.Single(store.Updated);
    Assert.Equal(AgentStatus.Failed, updated.Status);
    Assert.Equal(AgentFailureReason.Timeout, updated.FailureReason);
    _ = Assert.NotNull(updated.CompletedAt);
  }

  [Fact]
  public async Task RunAsync_ProviderThrows_ReturnsFailedOutcome_PersistsProviderError()
  {
    FakeAgentStore store = new();
    SubAgentSpawner spawner = MakeRunner(new ThrowingProvider(), store);

    AgentRunOutcome outcome = await spawner.RunAsync(Child(), CancellationToken.None);

    Assert.Equal(AgentStatus.Failed, outcome.Status);
    Assert.Equal(AgentFailureReason.ProviderError, outcome.Reason);

    AgentRecord updated = Assert.Single(store.Updated);
    Assert.Equal(AgentStatus.Failed, updated.Status);
    Assert.Equal(AgentFailureReason.ProviderError, updated.FailureReason);
  }

  // There is no tool-iteration cap anymore: a child that keeps calling tools runs
  // until its time budget elapses, so a looping model surfaces as Failed(Timeout).
  [Fact]
  public async Task RunAsync_LoopingChild_TimesOut_ReturnsFailedOutcome_PersistsFailedTimeout()
  {
    FakeAgentStore store = new();
    SubAgentSpawner spawner = MakeRunner(new LoopingProvider(), store,
        options: new SubAgentOptions(DefaultModel: "default/sub-model",
            ChildTimeout: TimeSpan.FromMilliseconds(250)),
        tools: new ToolRegistry([new FakeTool("loop", "again")]));

    AgentRunOutcome outcome = await spawner.RunAsync(Child(taskPrompt: "loop forever"), CancellationToken.None);

    Assert.Equal(AgentStatus.Failed, outcome.Status);
    Assert.Equal(AgentFailureReason.Timeout, outcome.Reason);

    AgentRecord updated = Assert.Single(store.Updated);
    Assert.Equal(AgentStatus.Failed, updated.Status);
    Assert.Equal(AgentFailureReason.Timeout, updated.FailureReason);
  }

  // --- terminal persistence fault ---

  [Fact]
  public async Task RunAsync_TerminalUpdateFails_ThrowsInfrastructureError()
  {
    FakeAgentStore store = new() { UpdateFailure = Result.Failure<string>(new DomainError("StorageDown", "agent store unavailable.")) };
    FakeProvider provider = new(
        Result.Success(new ModelResponse("report", [])));
    SubAgentSpawner spawner = MakeRunner(provider, store);

    _ = await Assert.ThrowsAsync<InvalidOperationException>(
        () => spawner.RunAsync(Child(), CancellationToken.None));
  }

  // --- report overflow ---

  [Fact]
  public async Task RunAsync_ReportOver50KB_AnnotationAppendedToPersistedReportAndOutcome()
  {
    string bigReport = new('x', 52_000);
    FakeAgentStore store = new();
    FakeProvider provider = new(
        Result.Success(new ModelResponse(bigReport, [])));
    SubAgentSpawner spawner = MakeRunner(provider, store);

    AgentRunOutcome outcome = await spawner.RunAsync(Child(taskPrompt: "big task"), CancellationToken.None);

    Assert.Equal(AgentStatus.Completed, outcome.Status);
    Assert.StartsWith(bigReport, outcome.Report, StringComparison.Ordinal);
    Assert.Contains("[agent] note: report exceeded 50 KB", outcome.Report, StringComparison.Ordinal);

    AgentRecord updated = Assert.Single(store.Updated);
    Assert.StartsWith(bigReport, updated.FinalReport, StringComparison.Ordinal);
    Assert.Contains("[agent] note: report exceeded 50 KB", updated.FinalReport, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RunAsync_ReportUnder50KB_NoAnnotation()
  {
    FakeAgentStore store = new();
    FakeProvider provider = new(
        Result.Success(new ModelResponse("compact report", [])));
    SubAgentSpawner spawner = MakeRunner(provider, store);

    AgentRunOutcome outcome = await spawner.RunAsync(Child(), CancellationToken.None);

    Assert.Equal(AgentStatus.Completed, outcome.Status);
    Assert.DoesNotContain("[agent] note:", outcome.Report, StringComparison.Ordinal);
  }
}
