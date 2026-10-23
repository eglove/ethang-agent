using System.Text.Json;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.ChildHost;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;
using Ag = eThangAgent.AgentDomain.Agent;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>The compaction handoff emit (issue #234 task 5): a turn whose agent ends with
///     HandoffPending emits a session-request through the SAME channel the new_session
///     tool uses — BEFORE the settle, so the app holds the new session's id before the
///     tab replacement — with an EMPTY prompt (seed-only launch: no auto first turn) and
///     the handoff summary as the seed. The pending flag is cleared after acting.
///     <para>The rig is production-faithful: the holder builds the root agent (the way the
///     first turn does), the handler resolves holder.Current, and the compaction fires
///     through a REAL usage report (the accountant starts empty, so the first iteration
///     makes a tool call whose usage report drives iteration 2 over the ceiling).</para></summary>
public class ChildHostHandoffEmitTests
{
  private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

  private static readonly ModelConfig Config =
      ModelConfig.Create("scripted/model", null, 4096, 0f, 1000).Value!;

  /// <summary>Answers in queued order: the turn's first (tool-call) response, the
  ///     summarizer call, then the turn's final answer.</summary>
  private sealed class QueueProvider(params Result<ModelResponse>[] responses) : IModelProvider
  {
    private readonly Queue<Result<ModelResponse>> _responses = new(responses);

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
        => Task.FromResult(_responses.Count > 0 ? _responses.Dequeue()
            : Result.Success(new ModelResponse("final", [])));

    public Task<Result<ModelResponse>> SendStreamingAsync(ModelConfig config, ModelRequest request,
        Action<string>? onContentDelta = null, Action<string>? onReasoningDelta = null,
        CancellationToken ct = default)
        => SendAsync(config, request, ct);
  }

  private sealed class EmptyToolRegistry : IToolRegistry
  {
    public ITool? Find(string name) => null;
    public IReadOnlyList<ToolDefinition> Definitions => [];
  }

  /// <summary>A conversation whose plan evicts the middle at window 1000 (target 40%):
  ///     the first user message stays protected, the 15000-char middle is evicted, and
  ///     six small recent exchanges form the kept tail.</summary>
  private static Conversation ConversationWithEvictableMiddle()
  {
    Conversation conversation = new();
    conversation.AddUserMessage("task");
    conversation.AddAssistantMessage(new string('x', 5000));
    conversation.AddUserMessage(new string('y', 5000));
    conversation.AddAssistantMessage(new string('z', 5000));
    for (int i = 0; i < 6; i++)
    {
      conversation.AddUserMessage($"recent {i}");
      conversation.AddAssistantMessage($"answer {i}");
    }

    return conversation;
  }

  private static async Task WaitUntilAsync(Func<bool> condition, string because)
  {
    DateTimeOffset deadline = DateTimeOffset.UtcNow + Limit;
    while (!condition())
    {
      if (DateTimeOffset.UtcNow > deadline)
      {
        Assert.Fail("timed out: " + because);
      }

      await Task.Delay(50, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }
  }

  /// <summary>The rig: holder-built agent (compactor in the dependencies), the handler
  ///     resolving holder.Current lazily, and the channel in the container.</summary>
  private static (ServiceProvider Services, RootRunHost Runner, Ag Agent, Conversation Conversation)
      BuildRig(DefaultContextCompactor compactor, IModelProvider provider, Conversation conversation)
  {
    RootDependencies dependencies = new(ContextCompactor: compactor);
    RootAgentHolder holder = new(provider, conversation, new EmptyToolRegistry(), dependencies: dependencies);
    Ag agent = holder.Build(null, Config); // the way the first turn builds the root
    ServiceProvider services = new ServiceCollection()
        .AddSingleton<NewSessionRequestChannel>()
        .AddSingleton(conversation)
        .AddSingleton(holder)
        .AddSingleton(sp => new SendMessageCommandHandler(
            agent: sp.GetRequiredService<RootAgentHolder>().Current,
            conversation: conversation))
        .BuildServiceProvider();
    RootRunHost runner = new(services, AgentId.NewId(), conversation);
    return (services, runner, agent, conversation);
  }

  [Fact]
  public async Task TurnEndingWithHandoffPending_EmitsSeedOnlySessionRequestBeforeTheSettle()
  {
    Conversation conversation = ConversationWithEvictableMiddle();
    int countBefore = conversation.Messages.Count;
    // Call 1: the turn's first response — a tool call whose usage report (800 input at
    // window 1000) puts iteration 2 at the 80% ceiling. Call 2: the summarizer.
    // Call 3: the turn's final answer.
    QueueProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("c1", "missing", "{}")],
            Usage: new TokenUsage(800, 10))),
        Result.Success(new ModelResponse("the handoff summary", [])),
        Result.Success(new ModelResponse("final", [])));
    DefaultContextCompactor compactor = new(
        new RecordingFactory(provider), () => null, handoffEnabled: () => true);
    (ServiceProvider services, RootRunHost runner, Ag agent, Conversation _) =
        BuildRig(compactor, provider, conversation);

    try
    {
      List<RootTurnEvent> events = [];
      Task<Result<string>> turn = runner.RunTurnAsync(
          new RootTurnRequest("turn-handoff", "go", []), events.Add,
          TestContext.Current.CancellationToken);

      // The handoff emit pends on the app's answer; the session-request event proves it
      // fired BEFORE the settle.
      await WaitUntilAsync(() => events.Any(e => e.Kind == "session-request"),
          "the session-request never arrived; events: " + string.Join(" | ", events.Select(e => e.Kind)))
          .ConfigureAwait(true);
      int requestIndex = events.FindIndex(e => e.Kind == "session-request");
      RootTurnEvent request = events[requestIndex];
      SessionRequestPayload payload = JsonSerializer.Deserialize<SessionRequestPayload>(request.Payload)!;

      Assert.Equal("", payload.Prompt); // seed-only launch: no auto first turn
      Assert.Equal("the handoff summary", payload.HandoffSummary);

      // The app's answer: the new session's id.
      runner.CompleteSessionRequest(payload.RequestId, Result.Success("new-session-id"));

      Result<string> result = await turn.WaitAsync(Limit, TestContext.Current.CancellationToken)
          .ConfigureAwait(true);
      Assert.True(result.IsSuccess, result.Error?.Message);

      // The session-request came BEFORE the settle: the app holds the id before the
      // tab replacement drains the turn.
      int settledIndex = events.FindIndex(e => e.Kind == "settled");
      Assert.True(settledIndex > requestIndex,
          "events: " + string.Join(" | ", events.Select(e => e.Kind)));

      // The pending flag was consumed: the host acted on it.
      Assert.False(agent.HandoffPending);

      // The old conversation was NOT compacted in place: no summary message landed in
      // it (the handoff outcome left the transcript intact).
      Assert.DoesNotContain(conversation.Messages, m => m.IsSummary);
      // user + assistant(tool call) + tool result + assistant(final) + handoff line
      Assert.Equal(countBefore + 5, conversation.Messages.Count);
    }
    finally
    {
      await services.DisposeAsync().ConfigureAwait(true);
      runner.Dispose();
    }
  }

  [Fact]
  public async Task TurnWithoutHandoff_CompactsInPlace_AndEmitsNoSessionRequest()
  {
    Conversation conversation = ConversationWithEvictableMiddle();
    QueueProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("c1", "missing", "{}")],
            Usage: new TokenUsage(800, 10))),
        Result.Success(new ModelResponse("the handoff summary", [])),
        Result.Success(new ModelResponse("final", [])));
    DefaultContextCompactor compactor = new(
        new RecordingFactory(provider), () => null, handoffEnabled: () => false);
    (ServiceProvider services, RootRunHost runner, Ag agent, Conversation _) =
        BuildRig(compactor, provider, conversation);

    try
    {
      List<RootTurnEvent> events = [];
      Result<string> result = await runner.RunTurnAsync(
          new RootTurnRequest("turn-plain", "go", []), events.Add,
          TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken)
          .ConfigureAwait(true);

      Assert.True(result.IsSuccess, result.Error?.Message);
      Assert.DoesNotContain(events, e => e.Kind == "session-request");
      Assert.False(agent.HandoffPending);
      Assert.Contains(conversation.Messages, m => m.IsSummary); // compacted in place
    }
    finally
    {
      await services.DisposeAsync().ConfigureAwait(true);
      runner.Dispose();
    }
  }

  /// <summary>The provider factory the real compactor resolves its summarizer through.</summary>
  private sealed class RecordingFactory(IModelProvider provider) : IModelProviderFactory
  {
    public IModelProvider Create(ModelConfig config) => provider;
  }
}
