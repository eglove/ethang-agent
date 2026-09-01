using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>R1 guard: the composition's grant surface reads ActionNames (the literal
///     list) because resolving the provider during composition would re-enter its
///     singleton. This test keeps the literal list honest against the real Actions.</summary>
public class AgentCapabilityProviderSurfaceTests
{
  [Fact]
  public void ActionNames_MatchTheActualActions()
  {
    AgentCapabilityProvider provider = MakeBare();
    string[] actual = [.. provider.Actions.Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal)];
    string[] declared = [.. AgentCapabilityProvider.ActionNames.OrderBy(n => n, StringComparer.Ordinal)];
    Assert.Equal(actual, declared);
  }

  /// <summary>The provider with every collaborator faked: only the Actions surface matters.</summary>
  private static AgentCapabilityProvider MakeBare()
      => new(new NoSpawn(), new NoQueries(), () => throw new InvalidOperationException("not exercised"), runtime: null, links: null);

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
}
