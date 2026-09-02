using eThangAgent.SharedKernel;
namespace eThangAgent.AgentDomain.Tests;

/// <summary>W4.3: the capability door — the broadcast actions are part of the
///     pinned ActionNames literal (the grant-surface computation reads the literal,
///     not the instance), and their descriptors carry the pinned receipt contracts
///     so the model-facing guide teaches them verbatim.</summary>
public class NotifyActionSurfaceTests
{
  [Fact]
  public void ActionNames_IncludeTheBroadcastActions()
  {
    Assert.Contains("notify-subtree", AgentCapabilityProvider.ActionNames);
    Assert.Contains("notify-ancestors", AgentCapabilityProvider.ActionNames);
  }

  [Fact]
  public void BroadcastDescriptors_TeachTheReceiptContracts()
  {
    AgentCapabilityProvider provider = MakeBare();
    string subtree = provider.Actions.Single(a => a.Name == "notify-subtree").Description;
    string ancestors = provider.Actions.Single(a => a.Name == "notify-ancestors").Description;

    Assert.Contains("hop=<n> to=<agent-id> delivered|NotRunning|MailboxFull", subtree, StringComparison.Ordinal);
    Assert.Contains("reached=<count> delivered=<count>", subtree, StringComparison.Ordinal);
    Assert.Contains("hop=<n> to=<agent-id> delivered|NotRunning|MailboxFull", ancestors, StringComparison.Ordinal);
    Assert.Contains("reached=root", ancestors, StringComparison.Ordinal);
  }

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

    public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<IReadOnlyList<AgentRecord>>(new DomainError("Unused", "not exercised here")));
  }
}
