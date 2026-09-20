using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Child-run persistence when the child shrinks its own context mid-run
///     (context_edit tool): the whole transcript is REPLACED, not appended - a
///     mid-run shrink would make the append-slice baseline double-count. The
///     sentinel contract mirrors the root loop's.</summary>
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
    // Exactly one append (the final assistant answer); the shrink turn contributed
    // ZERO appends because the transcript was replaced wholesale.
    // Replace-everything: the shrink turn's messages AND the final answer ride in the
    // replacement, so the append path contributes nothing.
    Assert.Empty(store.AppendedMessages);
    Assert.Equal("child report", store.ReplacedTranscripts[0].Messages[^1].Content);
    Assert.NotEmpty(store.ReplacedTranscripts);
    (AgentId id, IReadOnlyList<Message> messages) = store.ReplacedTranscripts.Single();
    Assert.Equal(outcome.ChildId, id);
    // post-shrink transcript: user task, assistant call, shrink tool result, final answer
    Assert.Equal(4, messages.Count);
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
