#pragma warning disable xUnit1051 // seeding fakes: no CancellationToken parameter exists on AppendMessageAsync; the analyzer flags omitted CTN args regardless

using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Resume contract for child runs: fresh runs send the task prompt; runs over a
///     persisted transcript send only the watchdog wrap-up nudge and append back only their
///     delta; failure paths persist the partial delta; teardown forgets the heartbeat.</summary>
public class SubAgentSpawnerResumeTests
{
  private static AgentRecord Child() => AgentRecord.Spawned(
      AgentId.NewId(), AgentId.NewId(), 1, "test-model", "test", "do the thing",
      DateTimeOffset.UtcNow);

  private static SubAgentSpawner Spawner(FakeAgentStore store, IModelProvider provider,
      IAgentHeartbeat? heartbeat = null) => new(
      new SubAgentServices(
          new FakeModelProviderFactory(provider), store,
          new ToolRegistry([new FakeTool("t", "ok")]),
          new StaticTestSystemPrompt(), new SubAgentOptions("test-model"),
          Heartbeat: heartbeat));


  private sealed class StaticTestSystemPrompt : ISystemPromptProvider
  {
    public string Build() => "test system prompt";
  }

  private sealed class CountingHeartbeat : IAgentHeartbeat
  {
    public bool ForgetCalled { get; private set; }

    public void Beat(AgentId agentId)
    {
    }

    public bool TryGetLastBeat(AgentId agentId, out DateTimeOffset lastBeat)
    {
      lastBeat = DateTimeOffset.UtcNow;
      return true;
    }

    public void Forget(AgentId agentId) => ForgetCalled = true;
  }

  [Fact]
  public async Task FreshRun_NoPersistedTranscript_SendsTaskPrompt()
  {
    FakeAgentStore store = new();
    FakeProvider provider = new(Result.Success(new ModelResponse("done", [])));
    AgentRecord child = Child();
    _ = await store.SaveAsync(child);

    AgentRunOutcome outcome = await Spawner(store, provider).RunAsync(child, ct: TestContext.Current.CancellationToken);

    Assert.Equal(AgentStatus.Completed, outcome.Status);
    ModelRequest request = provider.RequestsSeen[0];
    Message lastUser = request.Messages.Last(m => m.Role is Role.User);
    Assert.Contains("do the thing", lastUser.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ResumedRun_WithPersistedTranscript_SendsOnlyNudge()
  {
    FakeAgentStore store = new();
    AgentRecord child = Child();
    _ = await store.SaveAsync(child);
    _ = await store.AppendMessageAsync(child.Id, new Message(Role.User, "do the thing", DateTimeOffset.UtcNow));
    _ = await store.AppendMessageAsync(child.Id, new Message(Role.Assistant, "half done", DateTimeOffset.UtcNow));
    FakeProvider provider = new(Result.Success(new ModelResponse("final report", [])));

    AgentRunOutcome outcome = await Spawner(store, provider).RunAsync(child, ct: TestContext.Current.CancellationToken);

    Assert.Equal(AgentStatus.Completed, outcome.Status);
    ModelRequest request = provider.RequestsSeen[0];
    Assert.Equal(3, request.Messages.Count);
    Message lastUser = request.Messages.Last(m => m.Role is Role.User);
    Assert.Contains(SubAgentSpawner.WrapUpNudgeSentinel, lastUser.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("do the thing", lastUser.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task InterruptedRun_PersistsPartialTranscriptDeltaOnly()
  {
    FakeAgentStore store = new();
    AgentRecord child = Child();
    _ = await store.SaveAsync(child);
    _ = await store.AppendMessageAsync(child.Id, new Message(Role.User, "do the thing", DateTimeOffset.UtcNow));

    // The 250 ms CTS is the reachable completion path (deadlock vigilance): without it
    // the run would wait out the full 300 s ChildTimeout budget before failing.
    using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(250));
    AgentRunOutcome outcome = await Spawner(store, new BlockingProvider()).RunAsync(child, ct: cts.Token);

    Assert.Equal(AgentStatus.Failed, outcome.Status);
    Assert.Equal(AgentFailureReason.Interrupted, outcome.Reason);
    int taskPromptAppends = store.AppendedMessages.Count(a => a.AgentId == child.Id
        && a.Message.Role is Role.User && a.Message.Content == "do the thing");
    Assert.Equal(1, taskPromptAppends); // the seed, never duplicated by the failure path
  }

  [Fact]
  public async Task Run_WithHeartbeat_ForgetsEntryOnSettle()
  {
    FakeAgentStore store = new();
    CountingHeartbeat heartbeat = new();
    AgentRecord child = Child();
    _ = await store.SaveAsync(child);

    _ = await Spawner(store, new FakeProvider(Result.Success(new ModelResponse("done", []))), heartbeat).RunAsync(child, ct: TestContext.Current.CancellationToken);

    Assert.True(heartbeat.ForgetCalled);
  }

  [Fact]
  public async Task ResumedRun_Success_AppendsOnlyItsDelta()
  {
    FakeAgentStore store = new();
    AgentRecord child = Child();
    _ = await store.SaveAsync(child);
    _ = await store.AppendMessageAsync(child.Id, new Message(Role.User, "do the thing", DateTimeOffset.UtcNow));
    int baseline = 1;

    _ = await Spawner(store, new FakeProvider(Result.Success(new ModelResponse("done", [])))).RunAsync(child, ct: TestContext.Current.CancellationToken);

    Result<IReadOnlyList<Message>> transcriptResult = await store.GetTranscriptAsync(child.Id);
    IReadOnlyList<Message> transcript = transcriptResult.Value!;
    int nudgeAndAnswer = transcript.Count - baseline;
    Assert.Equal(3, transcript.Count); // seed + nudge + assistant answer, no duplicate seed
    Assert.True(nudgeAndAnswer >= 2);
  }
}
