using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>ISSUE #267 — the request prefix is provably stable within a turn.
///     <para>The system prompt and the tool definitions are the LEADING bytes of every
///     request, so a value that changes between iterations invalidates the provider's
///     prompt cache from its first changed character: every later token re-billed at
///     full input price, invisibly. Before the fix the loop called
///     <c>_systemPrompt.Build()</c> at three sites and read <c>_tools.Definitions</c> at
///     two, all INSIDE the iteration loop, so any impure provider rewrote the prefix and
///     nothing could notice.</para>
///     <para>These tests pin the invariant directly: a provider whose Build() changes on
///     every call must still produce a byte-identical prompt across every iteration of
///     one turn, and the NEXT turn must observe the change (the deliberate boundary).</para></summary>
public class AgentRequestPrefixStabilityTests
{
  private static readonly ModelConfig Config = ModelConfig.Create("test-model", 100, 0.5f, 1000).Value!;

  /// <summary>A provider that is DELIBERATELY impure: every Build() call returns
  ///     different text. This is the worst case the fix must absorb - it stands in for a
  ///     provider that reads a store, a file, or a hot-reloaded listing.</summary>
  private sealed class DriftingPromptProvider : ISystemPromptProvider
  {
    public int Calls { get; private set; }

    public string Build() => $"prompt-render-{++Calls}";
  }

  /// <summary>Records every request the loop sends, so the prefix each iteration saw is
  ///     directly comparable.</summary>
  private sealed class RecordingProvider(IReadOnlyList<ModelResponse> responses) : IModelProvider
  {
    private int _calls;

    public List<ModelRequest> Requests { get; } = [];

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
    {
      Requests.Add(request);
      int index = Math.Min(_calls, responses.Count - 1);
      _calls++;
      return Task.FromResult(Result.Success(responses[index]));
    }

    public Task<Result<ModelResponse>> SendStreamingAsync(ModelConfig config, ModelRequest request,
        Action<string>? onContentDelta = null, Action<string>? onReasoningDelta = null, CancellationToken ct = default)
        => SendAsync(config, request, ct);
  }

  private sealed class NoopTool(string name) : ITool
  {
    public ToolDefinition Definition { get; } = new(name, "test tool", [], ["timeoutSeconds"]);

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
        => Task.FromResult(new ToolResult("ok", false));
  }

  /// <summary>One turn with two tool-call iterations: three provider calls, so three
  ///     chances for a re-rendered prompt to leak into the wire.</summary>
  private static (RecordingProvider Provider, Agent Agent, DriftingPromptProvider Prompt) RunMultiIterationTurn()
  {
    Conversation conversation = new();
    conversation.AddUserMessage("seed task");
    DriftingPromptProvider prompt = new();
    RecordingProvider provider = new(
    [
      new ModelResponse(null, [new ToolCallRequest("c1", "first", "{}")]),
      new ModelResponse(null, [new ToolCallRequest("c2", "second", "{}")]),
      new ModelResponse("final", []),
    ]);
    Agent agent = new(provider, conversation, Config,
        new ToolRegistry([new NoopTool("first"), new NoopTool("second")]),
        new AgentOptions { SystemPrompt = prompt });
    return (provider, agent, prompt);
  }

  [Fact]
  public async Task SystemPrompt_IsByteIdentical_AcrossEveryIterationOfOneTurn()
  {
    (RecordingProvider provider, Agent agent, _) = RunMultiIterationTurn();

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(3, provider.Requests.Count);
    string first = provider.Requests[0].SystemPrompt!;
    Assert.All(provider.Requests, request => Assert.Equal(first, request.SystemPrompt));
  }

  [Fact]
  public async Task SystemPrompt_IsRenderedOncePerTurn_NotOncePerIteration()
  {
    (_, Agent agent, DriftingPromptProvider prompt) = RunMultiIterationTurn();

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    // Three provider calls, one render: the snapshot is taken at the turn's start.
    Assert.Equal(1, prompt.Calls);
  }

  [Fact]
  public async Task SystemPrompt_ChangeTakesEffect_AtTheNextTurnBoundary()
  {
    (RecordingProvider provider, Agent agent, DriftingPromptProvider prompt) = RunMultiIterationTurn();

    _ = await agent.SendMessage("first turn", ct: TestContext.Current.CancellationToken);
    int rendersAfterFirstTurn = prompt.Calls;
    _ = await agent.SendMessage("second turn", ct: TestContext.Current.CancellationToken);

    // The second turn re-renders exactly once and every request in it agrees.
    Assert.Equal(rendersAfterFirstTurn + 1, prompt.Calls);
    List<ModelRequest> secondTurn = [.. provider.Requests.Skip(3)];
    Assert.NotEmpty(secondTurn);
    string second = secondTurn[0].SystemPrompt!;
    Assert.All(secondTurn, request => Assert.Equal(second, request.SystemPrompt));
    // And the boundary is REAL: the new turn's prompt differs from the old turn's.
    Assert.NotEqual(provider.Requests[0].SystemPrompt, second);
  }

  [Fact]
  public async Task ToolDefinitions_AreIdentical_AcrossEveryIterationOfOneTurn()
  {
    (RecordingProvider provider, Agent agent, _) = RunMultiIterationTurn();

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    string first = Render(provider.Requests[0].Tools!);
    Assert.All(provider.Requests, request => Assert.Equal(first, Render(request.Tools!)));
  }

  [Fact]
  public async Task ToolDefinitions_AreStable_WhenTheRegistryIsMutatedMidTurn()
  {
    // The audit's "adding/removing/renaming a tool mid-session" row: the registry is
    // read once per turn, so a tool registered DURING a turn (the hot-reload shape)
    // cannot reach the wire until the next turn.
    Conversation conversation = new();
    conversation.AddUserMessage("seed task");
    MutableRegistry registry = new([new NoopTool("first")]);
    // The mutation rides the FIRST tool's execution - mid-turn, after the snapshot.
    registry.Add(new MutatingTool("first", () => registry.Add(new NoopTool("late"))));
    RecordingProvider provider = new(
    [
      new ModelResponse(null, [new ToolCallRequest("c1", "first", "{}")]),
      new ModelResponse("final", []),
    ]);
    Agent agent = new(provider, conversation, Config, registry, new AgentOptions());

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    string first = Render(provider.Requests[0].Tools!);
    Assert.All(provider.Requests, request => Assert.Equal(first, Render(request.Tools!)));
    Assert.DoesNotContain("late", first, StringComparison.Ordinal);
  }

  [Fact]
  public async Task NoSystemPromptProvider_Wired_StaysNull_EveryIteration()
  {
    Conversation conversation = new();
    conversation.AddUserMessage("seed task");
    RecordingProvider provider = new(
    [
      new ModelResponse(null, [new ToolCallRequest("c1", "first", "{}")]),
      new ModelResponse("final", []),
    ]);
    Agent agent = new(provider, conversation, Config, new ToolRegistry([new NoopTool("first")]),
        new AgentOptions());

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    // Legacy wiring stays byte-identical: no provider means no prompt, never a
    // synthesized one.
    Assert.All(provider.Requests, request => Assert.Null(request.SystemPrompt));
  }

  /// <summary>A tool that performs a side effect when it runs, so the test can mutate
  ///     the registry strictly INSIDE the turn (after the prefix snapshot).</summary>
  private sealed class MutatingTool(string name, Action onExecute) : ITool
  {
    public ToolDefinition Definition { get; } = new(name, "test tool", [], ["timeoutSeconds"]);

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
      onExecute();
      return Task.FromResult(new ToolResult("ok", false));
    }
  }

  /// <summary>A registry that can be mutated after construction, standing in for the
  ///     hot-reload surfaces (a tool granted mid-session).</summary>
  private sealed class MutableRegistry(IEnumerable<ITool> tools) : IToolRegistry
  {
    private readonly List<ITool> _tools = [.. tools];

    public void Add(ITool tool) => _tools.Add(tool);

    public ITool? Find(string name) => _tools.FirstOrDefault(t => t.Definition.Name == name);

    public IReadOnlyList<ToolDefinition> Definitions => [.. _tools.Select(t => t.Definition)];
  }

  private static string Render(IReadOnlyList<ToolDefinition> tools) =>
      string.Join("\n", tools.Select(t => t.Name + "|" + t.Description));
}
