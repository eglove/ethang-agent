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

  /// <summary>Issue #334: nothing on this surface starts an agent. The spawn and fanout
  ///     actions are gone, and putting either back fails here.</summary>
  [Fact]
  public void ActionNames_ExposeNoSpawnAndNoFanout()
  {
    AgentCapabilityProvider provider = MakeBare();
    Assert.DoesNotContain("spawn", provider.Actions.Select(a => a.Name));
    Assert.DoesNotContain("fanout", provider.Actions.Select(a => a.Name));
    Assert.DoesNotContain("spawn", AgentCapabilityProvider.ActionNames);
    Assert.DoesNotContain("fanout", AgentCapabilityProvider.ActionNames);
    Assert.DoesNotContain(provider.Actions, a => a.Description.Contains("agent.spawn", StringComparison.Ordinal));
  }

  /// <summary>The provider with every collaborator faked: only the Actions surface matters.</summary>
  private static AgentCapabilityProvider MakeBare()
      => new(new AgentProviderServices(new NoQueries()));

  private sealed class NoQueries : IAgentQueries
  {
    public Task<Result<AgentRecord>> GetStatus(AgentId id, CancellationToken ct = default)
        => throw new NotSupportedException("not exercised");

    public Task<Result<string>> GetResult(AgentId id, CancellationToken ct = default)
        => throw new NotSupportedException("not exercised");
  }
}
