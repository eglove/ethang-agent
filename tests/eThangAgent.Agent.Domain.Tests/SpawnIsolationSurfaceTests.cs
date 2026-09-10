using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>The spawn/fanout actions advertise isolateInWorktree and parse it strictly:
///     a boolean parameter, strict-kind parsing, true forwarded to the request,
///     and descriptions state the isolation contract.</summary>
public class SpawnIsolationSurfaceTests
{
  [Fact]
  public async Task SpawnDispatch_ForwardsIsolateInWorktree_ToTheRequest()
  {
    RecordingSpawn rec = new();
    AgentCapabilityProvider provider = new(rec, new NoQueries(), Root);
    CapabilityInvocationResult r = await provider.InvokeAsync("spawn",
        /*lang=json,strict*/ """{"taskPrompt":"do a thing","isolateInWorktree":true}""",
        ct: TestContext.Current.CancellationToken);

    Assert.False(r.IsError);
    Assert.True(Assert.Single(rec.Requests).IsolateInWorktree);
  }

  [Fact]
  public async Task SpawnDispatch_BoolParsingIsStrict()
  {
    AgentCapabilityProvider provider = new(new RecordingSpawn(), new NoQueries(), () => throw new InvalidOperationException("not exercised"));
    CapabilityInvocationResult r = await provider.InvokeAsync("spawn",
        /*lang=json,strict*/ """{"taskPrompt":"t","isolateInWorktree":"yes"}""",
        ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsError);
    Assert.Contains("isolateInWorktree must be a boolean", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SpawnDispatch_UnknownParameterStillRefused()
  {
    AgentCapabilityProvider provider = new(new RecordingSpawn(), new NoQueries(), () => throw new InvalidOperationException("not exercised"));
    CapabilityInvocationResult r = await provider.InvokeAsync("spawn",
        /*lang=json,strict*/ """{"taskPrompt":"t","isolate":true}""",
        ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsError);
    Assert.Contains("unknown parameter(s): isolate", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Descriptions_StateTheIsolationContract()
  {
    AgentCapabilityProvider provider = MakeBare();
    ActionDescriptor spawn = provider.Actions.Single(a => a.Name == "spawn");
    Assert.Contains("isolateInWorktree", spawn.Description, StringComparison.Ordinal);
    Assert.Contains("Mutually exclusive", spawn.Description, StringComparison.Ordinal);
    ActionDescriptor fanout = provider.Actions.Single(a => a.Name == "fanout");
    string children = fanout.Parameters.Single(p => p.Name == "children").Description;
    Assert.Contains("isolateInWorktree", children, StringComparison.Ordinal);
  }

  private static AgentRecord Root()
      => AgentRecord.Spawned(AgentId.NewId(), null, 0, "test-model", null, "root task", DateTimeOffset.UtcNow);

  [Fact]
  public async Task FanoutDispatch_ForwardsChildIsolationFlag_ToTheRequest()
  {
    List<SpawnRequest> sent = [];
    AgentCapabilityProvider provider = new(new RecordingSpawn(), new NoQueries(), Root,
        fanout: (parent, requests, ct) =>
        {
          sent.AddRange(requests);
          return Task.FromResult("receipts: done");
        });

    CapabilityInvocationResult r = await provider.InvokeAsync("fanout",
        /*lang=json,strict*/ """{"children":[{"taskPrompt":"a","isolateInWorktree":true},{"taskPrompt":"b"}]}""",
        ct: TestContext.Current.CancellationToken);

    Assert.False(r.IsError);
    Assert.Equal(2, sent.Count);
    Assert.True(sent[0].IsolateInWorktree);
    Assert.False(sent[1].IsolateInWorktree);
  }

  private static AgentCapabilityProvider MakeBare()
      => new(new RecordingSpawn(), new NoQueries(), () => throw new InvalidOperationException("not exercised"));

  private sealed class RecordingSpawn : IAgentSpawnCommand
  {
    public List<SpawnRequest> Requests { get; } = [];

    public Task<Result<AgentId>> Execute(AgentRecord parent, SpawnRequest request, CancellationToken ct = default)
    {
      Requests.Add(request);
      return Task.FromResult(Result.Success(new AgentId(Guid.NewGuid())));
    }
  }

  private sealed class NoQueries : IAgentQueries
  {
    public Task<Result<AgentRecord>> GetStatus(AgentId id, CancellationToken ct = default)
        => throw new NotSupportedException("not exercised");

    public Task<Result<string>> GetResult(AgentId id, CancellationToken ct = default)
        => throw new NotSupportedException("not exercised");

    public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default)
        => throw new NotSupportedException("not exercised");
  }
}
