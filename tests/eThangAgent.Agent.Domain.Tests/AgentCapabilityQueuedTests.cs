using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Issue #238's read-model half: the queued state is first-class everywhere a
///     caller looks. agent.status renders status=queued (never a bare running lie),
///     agent.cancel accepts a queued child (nothing else could stop a parked spawn),
///     agent.send reaches it through the ordinary delivery path, and agent.result reports
///     NotComplete rather than pretending the child is unknown.</summary>
public class AgentCapabilityQueuedTests
{
  private static AgentRecord Parent()
      => AgentRecord.Spawned(AgentId.NewId(), null, 0, "prov/parent", null, "root task", DateTimeOffset.UtcNow);

  private static AgentRecord QueuedChild(AgentId parentId)
      => AgentRecord.Spawned(AgentId.NewId(), parentId, 1, "prov/child", "queued", "child task",
          DateTimeOffset.UtcNow) with
      { Status = AgentStatus.Queued };

  private sealed class NoSpawn : IAgentSpawnCommand
  {
    public Task<Result<AgentId>> Execute(AgentRecord parent, SpawnRequest request, CancellationToken ct = default)
        => Task.FromResult(Result.Success(AgentId.NewId()));
  }

  private sealed class SpawnReturning(AgentId id) : IAgentSpawnCommand
  {
    public Task<Result<AgentId>> Execute(AgentRecord parent, SpawnRequest request, CancellationToken ct = default)
        => Task.FromResult(Result.Success(id));
  }

  private sealed class FixedQueries(AgentRecord? child) : IAgentQueries
  {
    public Task<Result<AgentRecord>> GetStatus(AgentId id, CancellationToken ct = default)
        => Task.FromResult(child is not null && child.Id == id
            ? Result.Success(child)
            : Result.Failure<AgentRecord>(new DomainError("NotFound", $"No agent exists with id '{id}'.")));

    public Task<Result<string>> GetResult(AgentId id, CancellationToken ct = default)
        => Task.FromResult(child is not null && child.Id == id
            ? Result.Failure<string>(new DomainError("NotComplete", $"Agent '{id}' has not finished running."))
            : Result.Failure<string>(new DomainError("NotFound", $"No agent exists with id '{id}'.")));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId,
        CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([]));
  }

  private sealed class RecordingRuntime : IAgentRuntime
  {
    public List<AgentId?> Interrupted { get; } = [];

    public List<(Guid Id, string Text)> Delivered { get; } = [];

    public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success(record.Id));

    public Task<Result<AgentRunOutcome>> WhenSettledAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<AgentRunOutcome>(
            new DomainError("NotFound", $"agent '{id}' has no live or settled run owned by this runtime.")));

    public Task<Result<AgentId>> Resume(AgentId id, string message, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<AgentId>(new DomainError("NotRunning", "not settled.")));

    public void InterruptSubtree(AgentId rootOfSubtree) { }

    public void Interrupt(AgentId? childId = null) => Interrupted.Add(childId);
  }

  private static (AgentCapabilityProvider Provider, RecordingRuntime Runtime, AgentRecord Child)
      BuildProvider()
  {
    AgentRecord parent = Parent();
    AgentRecord child = QueuedChild(parent.Id);
    RecordingRuntime runtime = new();
    AgentCapabilityProvider provider = new(new AgentProviderServices(
        new NoSpawn(), new FixedQueries(child), () => parent, runtime));
    return (provider, runtime, child);
  }

  private static string TaskPromptJson(string prompt)
  {
    const string quote = "\"";
    return "{" + quote + "taskPrompt" + quote + ":" + quote + prompt + quote + "}";
  }

  private static string IdJson(AgentId id)
  {
    const string quote = "\"";
    return "{" + quote + "id" + quote + ":" + quote + id.Value.ToString("D") + quote + "}";
  }

  [Fact]
  public async Task Spawn_WhenTheChildWasQueued_RendersQueuedNotRunning()
  {
    // Issue #238 honesty rule: the spawn receipt must state the state the child is
    // actually in. A parked spawn reported as running is the same class of lie as
    // agent.wait handing a failure sentence back as a deliverable.
    AgentRecord parent = Parent();
    AgentRecord queued = QueuedChild(parent.Id);
    AgentCapabilityProvider provider = new(new AgentProviderServices(
        new SpawnReturning(queued.Id), new FixedQueries(queued), () => parent, new RecordingRuntime()));

    CapabilityInvocationResult result = await provider.InvokeAsync("spawn",
        TaskPromptJson("queued task"), TestContext.Current.CancellationToken);

    Assert.False(result.IsError, result.Content);
    Assert.Equal($"id={queued.Id} status=queued", result.Content);
  }

  [Fact]
  public async Task Status_QueuedChild_RendersQueuedStateLine()
  {
    (AgentCapabilityProvider provider, RecordingRuntime _, AgentRecord child) = BuildProvider();

    CapabilityInvocationResult result = await provider.InvokeAsync("status", IdJson(child.Id),
        TestContext.Current.CancellationToken);

    Assert.False(result.IsError, result.Content);
    Assert.Equal($"id={child.Id} status=queued", result.Content);
  }

  [Fact]
  public async Task Cancel_QueuedChild_InterruptsInsteadOfRefusingNotRunning()
  {
    (AgentCapabilityProvider provider, RecordingRuntime runtime, AgentRecord child) = BuildProvider();

    CapabilityInvocationResult result = await provider.InvokeAsync("cancel", IdJson(child.Id),
        TestContext.Current.CancellationToken);

    Assert.False(result.IsError, result.Content);
    Assert.Contains($"interrupted id={child.Id}", result.Content, StringComparison.Ordinal);
    Assert.Equal(child.Id, Assert.Single(runtime.Interrupted));
  }

  [Fact]
  public async Task Result_QueuedChild_ReportsNotComplete_NotNotFound()
  {
    (AgentCapabilityProvider provider, RecordingRuntime _, AgentRecord child) = BuildProvider();

    CapabilityInvocationResult result = await provider.InvokeAsync("result", IdJson(child.Id),
        TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [NotComplete]:", result.Content, StringComparison.Ordinal);
  }
}
