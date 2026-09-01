using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>agent.fanout (D12): the model-facing door to spawn graphs. The graph logic is
///     the application layer's SpawnGraphHandler; here we pin the action contract —
///     argument validation, NotAvailable without the seam, and passthrough rendering.</summary>
public class AgentFanoutTests
{
  private static AgentRecord Parent() => AgentRecord.Spawned(AgentId.NewId(), null, 0,
      "m", null, "t", DateTimeOffset.UtcNow);

  private static AgentCapabilityProvider Make(Func<AgentRecord, SpawnRequest[], CancellationToken, Task<string>>? fanout)
      => new(new NoSpawn(), new NoQueries(), Parent, runtime: null, links: null, fanout: fanout);

  private sealed class NoSpawn : IAgentSpawnCommand
  {
    public Task<Result<AgentId>> Execute(AgentRecord parent, SpawnRequest request, CancellationToken ct = default)
        => throw new NotSupportedException("not exercised");
  }

  private sealed class NoQueries : IAgentQueries
  {
    public Task<Result<AgentRecord>> GetStatus(AgentId id, CancellationToken ct = default)
        => throw new NotSupportedException("not exercised");

    public Task<Result<string>> GetResult(AgentId id, CancellationToken ct = default)
        => throw new NotSupportedException("not exercised");
  }

  [Fact]
  public async Task Fanout_WithoutSeam_NotAvailable()
  {
    AgentCapabilityProvider provider = Make(null);

    CapabilityInvocationResult result = await provider.InvokeAsync("fanout",
        /*lang=json,strict*/ "{\"children\":[{\"taskPrompt\":\"x\"}]}", TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [NotAvailable]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Fanout_ChildrenMissing_FailsValidation()
  {
    AgentCapabilityProvider provider = Make((_, _, _) => Task.FromResult("ok"));

    CapabilityInvocationResult result = await provider.InvokeAsync("fanout",
        /*lang=json,strict*/ "{}", TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Contains("'children' must be an array", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Fanout_ChildWithoutPrompt_FailsValidationNamingIndex()
  {
    AgentCapabilityProvider provider = Make((_, _, _) => Task.FromResult("ok"));

    CapabilityInvocationResult result = await provider.InvokeAsync("fanout",
        /*lang=json,strict*/ "{\"children\":[{\"taskPrompt\":\"a\"},{\"label\":\"no-prompt\"}]}", TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Contains("children[2].taskPrompt", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Fanout_Valid_PassesSpecsToTheSeam_AndRendersItsResult()
  {
    SpawnRequest[]? captured = null;
    AgentCapabilityProvider provider = Make((_, children, _) =>
    {
      captured = children;
      return Task.FromResult("graph completed; receipts: 2/2");
    });

    CapabilityInvocationResult result = await provider.InvokeAsync("fanout",
        /*lang=json,strict*/ "{\"children\":[{\"taskPrompt\":\"a\",\"model\":\"m1\"},{\"taskPrompt\":\"b\",\"label\":\"lb\"}]}",
        TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal("graph completed; receipts: 2/2", result.Content);
    Assert.NotNull(captured);
    Assert.Equal(2, captured.Length);
    Assert.Equal("a", captured[0].TaskPrompt);
    Assert.Equal("m1", captured[0].Model);
    Assert.Equal("b", captured[1].TaskPrompt);
    Assert.Equal("lb", captured[1].Label);
  }

  [Fact]
  public void ActionNames_IncludeFanout() => Assert.Contains("fanout", AgentCapabilityProvider.ActionNames);
}
