using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>W5.2: the agent.fanout boundary matrix, driven through the PRODUCTION
///     wiring shape (domain AgentCapabilityProvider -> composition fanout lambda ->
///     application SpawnGraphHandler), not the parser in isolation. Pins the BDD
///     children-argument outcomes: one well-formed child; labels; taskPrompt rejected
///     InvalidActionInput verbatim; malformed JSON and an empty list rejected at
///     validation; a depth violation surfaces DepthExceeded and fails the join
///     immediately; per-child model overrides honored.</summary>
public class FanoutBoundaryTests
{
  private sealed class RecordingSpawnCommand(Func<SpawnRequest, Result<AgentId>> reply) : IAgentSpawnCommand
  {
    public List<SpawnRequest> Requests { get; } = [];

    public Task<Result<AgentId>> Execute(AgentRecord parent, SpawnRequest request, CancellationToken ct = default)
    {
      Requests.Add(request);
      return Task.FromResult(reply(request));
    }
  }

  private sealed class OneShotRuntime : IAgentRuntime
  {
    public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success(record.Id));

    public Task<Result<AgentRunOutcome>> WhenSettledAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Success(new AgentRunOutcome(id, AgentStatus.Completed, null, "ok", "m/x", 1)));

    public Result<bool> Deliver(AgentId id, PendingMessage message) => Result.Success(true);

    public void Interrupt(AgentId? childId = null) { }

    public void InterruptSubtree(AgentId rootOfSubtree) { }
  }

  private sealed class NoQueries : IAgentQueries
  {
    public Task<Result<AgentRecord>> GetStatus(AgentId id, CancellationToken ct = default)
        => throw new NotSupportedException("not exercised");

    public Task<Result<string>> GetResult(AgentId id, CancellationToken ct = default)
        => throw new NotSupportedException("not exercised");

    public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<IReadOnlyList<AgentRecord>>(new DomainError("Unused", "not exercised here")));
  }

  private static AgentRecord Root() => AgentRecord.Spawned(AgentId.NewId(), null, 0,
      "prov/model", null, "root", DateTimeOffset.UtcNow);

  /// <summary>The composition's fanout seam, wired exactly as AgentComposition wires it
  ///     (graph handler over the same spawn seam, join failure rendered as an Error
  ///     line) - so these tests exercise the boundary the model actually reaches.</summary>
  private static (AgentCapabilityProvider Provider, RecordingSpawnCommand Spawns) Make(
      AgentRecord parent, Func<SpawnRequest, Result<AgentId>>? reply = null)
  {
    RecordingSpawnCommand spawns = new(reply ?? (_ => Result.Success(AgentId.NewId())));
    OneShotRuntime runtime = new();
    SpawnGraphHandler graph = new(spawns, runtime);
    AgentCapabilityProvider provider = new(spawns, new NoQueries(), () => parent, runtime,
        fanout: async (p, children, ct) =>
        {
          Result<SpawnGraphOutcome> joined = await graph.ExecuteAsync(p,
              new SpawnGraphRequest(Label: "", Children: children, Join: new JoinPolicy(true)), ct).ConfigureAwait(true);
          return joined.IsSuccess
              ? joined.Value.Render()
              : "Error [" + joined.Error.Code + "]: " + joined.Error.Message;
        });
    return (provider, spawns);
  }

  private static async Task<CapabilityInvocationResult> InvokeAsync(
      AgentCapabilityProvider provider, string json)
      => await provider.InvokeAsync("fanout", json, TestContext.Current.CancellationToken).ConfigureAwait(true);

  [Fact]
  public async Task Single_WellFormed_Child_Spawns_One_And_Completes_The_Join()
  {
    (AgentCapabilityProvider provider, RecordingSpawnCommand spawns) = Make(Root());

    CapabilityInvocationResult result = await InvokeAsync(provider,
        /*lang=json,strict*/ "{\"children\":[{\"taskPrompt\":\"x\"}]}").ConfigureAwait(true);

    Assert.False(result.IsError);
    Assert.DoesNotContain("Error [", result.Content, StringComparison.Ordinal);
    SpawnRequest request = Assert.Single(spawns.Requests);
    Assert.Equal("x", request.TaskPrompt);
  }

  [Fact]
  public async Task Children_Carrying_Labels_Spawn_Under_Their_Labels()
  {
    (AgentCapabilityProvider provider, RecordingSpawnCommand spawns) = Make(Root());

    CapabilityInvocationResult result = await InvokeAsync(provider,
        /*lang=json,strict*/ "{\"children\":[{\"taskPrompt\":\"a\",\"label\":\"a\"},{\"taskPrompt\":\"b\",\"label\":\"b\"}]}").ConfigureAwait(true);

    Assert.False(result.IsError);
    Assert.Equal(2, spawns.Requests.Count);
    Assert.Equal("a", spawns.Requests[0].Label);
    Assert.Equal("b", spawns.Requests[1].Label);
  }

  [Fact]
  public async Task Child_Missing_TaskPrompt_Is_Rejected_InvalidActionInput_Verbatim()
  {
    (AgentCapabilityProvider provider, RecordingSpawnCommand spawns) = Make(Root());

    CapabilityInvocationResult result = await InvokeAsync(provider,
        /*lang=json,strict*/ "{\"children\":[{\"taskPrompt\":\"a\"},{\"label\":\"no-prompt\"}]}").ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidActionInput]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("children[2].taskPrompt must be a non-empty string.", result.Content, StringComparison.Ordinal);
    Assert.Empty(spawns.Requests);
  }

  [Fact]
  public async Task Malformed_Json_Is_Rejected_At_Validation()
  {
    (AgentCapabilityProvider provider, _) = Make(Root());

    CapabilityInvocationResult result = await InvokeAsync(provider, "{not json").ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidActionInput]:", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Empty_Children_List_Is_Rejected_At_Validation()
  {
    (AgentCapabilityProvider provider, RecordingSpawnCommand spawns) = Make(Root());

    CapabilityInvocationResult result = await InvokeAsync(provider,
        /*lang=json,strict*/ "{\"children\":[]}").ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidActionInput]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("children", result.Content, StringComparison.Ordinal);
    Assert.Empty(spawns.Requests);
  }

  [Fact]
  public async Task Depth_Violation_Fails_The_Join_Immediately_Naming_DepthExceeded()
  {
    AgentRecord deepParent = AgentRecord.Spawned(AgentId.NewId(), null, 3,
        "prov/model", null, "root", DateTimeOffset.UtcNow);
    (AgentCapabilityProvider provider, RecordingSpawnCommand spawns) = Make(deepParent,
        _ => Result.Failure<AgentId>(new DomainError("DepthExceeded",
            "agent depth 3 is at the limit (3); children cannot spawn further")));

    CapabilityInvocationResult result = await InvokeAsync(provider,
        /*lang=json,strict*/ "{\"children\":[{\"taskPrompt\":\"a\"},{\"taskPrompt\":\"b\"}]}").ConfigureAwait(true);

    // The fanout boundary delivers join failures as error text inside the result
    // (the composition lambda renders the join outcome) - the substance is that the
    // START's own code reaches the model verbatim.
    Assert.StartsWith("Error [DepthExceeded]:", result.Content, StringComparison.Ordinal);
    // Failed starts fail the join immediately (the advertised contract): the second
    // child must never be started after the first start already failed.
    SpawnRequest request = Assert.Single(spawns.Requests);
    Assert.Equal("a", request.TaskPrompt);
  }

  [Fact]
  public async Task Per_Child_Model_Overrides_Are_Honored()
  {
    (AgentCapabilityProvider provider, RecordingSpawnCommand spawns) = Make(Root());

    CapabilityInvocationResult result = await InvokeAsync(provider,
        /*lang=json,strict*/ "{\"children\":[{\"taskPrompt\":\"a\",\"model\":\"m1\"},{\"taskPrompt\":\"b\",\"model\":\"m2\"}]}").ConfigureAwait(true);

    Assert.False(result.IsError);
    Assert.Equal(2, spawns.Requests.Count);
    Assert.Equal("m1", spawns.Requests[0].Model);
    Assert.Equal("m2", spawns.Requests[1].Model);
  }
}
