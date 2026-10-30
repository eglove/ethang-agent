using eThangAgent.CapabilityDomain;
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

  /// <summary>Issue #316: the six agent-to-agent messaging actions are GONE from the
  ///     surface, not merely undocumented. A retired action that still resolves is a working
  ///     message channel a future session would build on, which is exactly what the issue
  ///     exists to prevent. Pinned against BOTH the literal list the grant surface reads and
  ///     the real descriptors, so the two cannot drift apart on this.</summary>
  [Fact]
  public void ActionNames_ExcludeEveryRetiredMessagingAction()
  {
    string[] retired =
        ["send", "receipts", "route", "escalate", "notify-subtree", "notify-ancestors"];
    AgentCapabilityProvider provider = MakeBare();
    string[] actual = [.. provider.Actions.Select(a => a.Name)];

    foreach (string action in retired)
    {
      Assert.DoesNotContain(action, actual);
      Assert.DoesNotContain(action, AgentCapabilityProvider.ActionNames);
    }
  }

  /// <summary>Issue #316 D5: the DRAIN fact survives the retirement, and it is the fact the two
  ///     Desktop rendering contracts ride (the pinned steering echo's release and the tab
  ///     badge's clear). Retiring it would leave steering working while its rendering silently
  ///     broke - the worst shape of failure, because nothing errors. This pins that the event
  ///     still exists and still names the ids it consumed.</summary>
  [Fact]
  public void MailboxDrainedEvent_StillNamesTheIdsItConsumed()
  {
    Guid first = Guid.NewGuid();
    Guid second = Guid.NewGuid();
    MailboxDrainedEvent drained = new(AgentId.NewId(), DateTimeOffset.UtcNow, [first, second]);

    Assert.Equal([first, second], drained.MessageIds);
    Assert.Equal(2, drained.Count);
  }

  [Fact]
  public void ActionNames_ExposeResume()
  {
    AgentCapabilityProvider provider = MakeBare();
    Assert.Contains("resume", provider.Actions.Select(a => a.Name));
    Assert.Contains("resume", AgentCapabilityProvider.ActionNames);
  }

  [Fact]
  public void SpawnDescription_StatesCapabilitySurfaceRerooting()
  {
    AgentCapabilityProvider provider = MakeBare();
    ActionDescriptor spawn = provider.Actions.Single(a => a.Name == "spawn");
    Assert.Contains("ENTIRE tool surface", spawn.Description, StringComparison.Ordinal);
    Assert.Contains("inherits the parent's anchor", spawn.Description, StringComparison.Ordinal);
    Assert.DoesNotContain("does NOT re-root", spawn.Description, StringComparison.Ordinal);
    ActionDescriptor fanout = provider.Actions.Single(a => a.Name == "fanout");
    string children = fanout.Parameters.Single(p => p.Name == "children").Description;
    Assert.Contains("anchors that child's ENTIRE tool surface", children, StringComparison.Ordinal);
    Assert.DoesNotContain("Does not re-root the child's capability surface", children, StringComparison.Ordinal);
  }

  /// <summary>The provider with every collaborator faked: only the Actions surface matters.</summary>
  private static AgentCapabilityProvider MakeBare()
      => new(new AgentProviderServices(new NoSpawn(), new NoQueries(), () => throw new InvalidOperationException("not exercised")));

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
