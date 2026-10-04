using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Child-run persistence when the child shrinks its own context mid-run
///     (context_edit tool): the whole transcript is REPLACED, not appended - the
///     shrink's wholesale replacement supersedes the incremental appends that already
///     landed during the run (the sink's ReplaceAsync resets the flushed baseline).
///     The sentinel contract mirrors the root loop's.</summary>
public class SubAgentSpawnerShrinkTests
{
  private static readonly DateTimeOffset FixedNow = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

  private static AgentRecord Child(string taskPrompt = "do things")
      => AgentRecord.Spawned(AgentId.NewId(), null, 0, "default/sub-model", null, taskPrompt, FixedNow);

  private sealed class ShrinkingTool : ITool
  {
    public const string SentinelResult = "[context: shrank 1 message(s): removed last 1.]";

    public ToolDefinition Definition { get; } = new("context_edit", "test", [], ["timeoutSeconds"]);

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
      => Task.FromResult(new ToolResult(SentinelResult, false));
  }

  private static SubAgentSpawner MakeRunner(IModelProvider provider, FakeAgentStore store, IToolRegistry tools)
      => new(new SubAgentServices(
          new FakeModelProviderFactory(provider),
          store,
          tools,
          new StaticPromptProvider("guide text"),
          new SubAgentOptions(DefaultModel: "default/sub-model")));

  [Fact]
  public async Task RunAsync_ChildShrinks_ReplacesTranscript_NoDuplicateAppends()
  {
    FakeAgentStore store = new();
    ShrinkingTool tool = new();
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("c1", "context_edit", "{}")])),
        Result.Success(new ModelResponse("child report", [])));
    SubAgentSpawner spawner = MakeRunner(provider, store, new ToolRegistry([tool]));

    AgentRunOutcome outcome = await spawner.RunAsync(Child(), CancellationToken.None);

    Assert.Equal(AgentStatus.Completed, outcome.Status);
    // Incremental appends landed during the run (pre-shrink messages included); the
    // shrink then REPLACED the transcript wholesale, so the persisted frontier is the
    // post-shrink conversation — no duplicates, no stale rows.
    (AgentId id, IReadOnlyList<Message> messages) = store.ReplacedTranscripts.Single();
    Assert.Equal(outcome.ChildId, id);
    Assert.Equal(4, messages.Count);
    Assert.Equal("child report", messages[^1].Content);
  }

  [Fact]
  public async Task RunAsync_NoShrink_AppendPathUnchanged()
  {
    FakeAgentStore store = new();
    FakeProvider provider = new(
        Result.Success(new ModelResponse("child report", [])));
    SubAgentSpawner spawner = MakeRunner(provider, store, new ToolRegistry([]));

    _ = await spawner.RunAsync(Child(), CancellationToken.None);

    Assert.Empty(store.ReplacedTranscripts);
    Assert.Equal(2, store.AppendedMessages.Count);
  }
}
