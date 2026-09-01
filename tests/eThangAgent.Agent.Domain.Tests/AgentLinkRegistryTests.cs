using eThangAgent.SharedKernel;
namespace eThangAgent.AgentDomain.Tests;

/// <summary>Link semantics: consent required, resolve only through links, revocation exact.
///     The NotLinked contract of the source spec's error table is pinned here.</summary>
public class AgentLinkRegistryTests
{
  [Fact]
  public void Link_WithoutConsent_IsRefused()
  {
    AgentLinkRegistry registry = new();
    Result<LinkAddress> linked = registry.Link("peer", "container-a", "agent-1", consented: false);
    Assert.False(linked.IsSuccess);
    Assert.Equal("ConsentRequired", linked.Error.Code);
  }

  [Fact]
  public void Resolve_UnknownName_FailsNotLinked()
  {
    AgentLinkRegistry registry = new();
    Result<LinkAddress> resolved = registry.Resolve("nope");
    Assert.False(resolved.IsSuccess);
    Assert.Equal("NotLinked", resolved.Error.Code);
  }

  [Fact]
  public void Link_Resolve_Revoke_RoundTrip()
  {
    AgentLinkRegistry registry = new();
    _ = registry.Link("peer", "container-a", "agent-1", consented: true);

    Result<LinkAddress> resolved = registry.Resolve("peer");
    string address = resolved.Match(success => success.AgentAddress, error => throw new InvalidOperationException(error.Message));
    Assert.Equal("agent-1", address);

    Assert.True(registry.Revoke("peer").IsSuccess);
    Result<LinkAddress> after = registry.Resolve("peer");
    Assert.False(after.IsSuccess);
    Assert.Equal("NotLinked", after.Error.Code);
  }

  [Fact]
  public void Resolve_RevealsNothingBeyondTheAddress_TrustModel_R2_4()
  {
    AgentLinkRegistry registry = new();
    _ = registry.Link("peer", "container-a", "agent-1", consented: true);

    Result<LinkAddress> resolved = registry.Resolve("peer");
    Assert.True(resolved.IsSuccess);
    LinkAddress address = resolved.Value;
    // The linker's records carry consent state and a linked-at timestamp; Resolve
    // exposes exactly Name, Container, AgentAddress — nothing else (open question 6).
    Assert.Equal("peer", address.Name);
    Assert.Equal("container-a", address.Container);
    Assert.Equal("agent-1", address.AgentAddress);
    System.Reflection.PropertyInfo[] exposed = typeof(LinkAddress).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
    string[] names = [.. exposed.Select(pr => pr.Name).OrderBy(n => n, StringComparer.Ordinal)];
    Assert.Equal(["AgentAddress", "Container", "Name"], names);
  }

  [Fact]
  public void Revoke_UnknownName_FailsNotFound()
  {
    AgentLinkRegistry registry = new();
    Result<bool> revoked = registry.Revoke("ghost");
    Assert.False(revoked.IsSuccess);
    Assert.Equal("NotFound", revoked.Error.Code);
  }

  [Fact]
  public void Snapshot_ListsLiveLinks_AndReflectsRevocation()
  {
    AgentLinkRegistry registry = new();
    _ = registry.Link("alpha", "container-a", "00000000-0000-0000-0000-000000000001", consented: true);
    _ = registry.Link("beta", "container-b", "00000000-0000-0000-0000-000000000002", consented: true);

    IReadOnlyList<LinkAddress> snapshot = registry.Snapshot;
    Assert.Equal(2, snapshot.Count);
    // Newest first: the most recently created link leads the dialog's list.
    Assert.Equal("beta", snapshot[0].Name);
    Assert.Equal("alpha", snapshot[1].Name);

    _ = registry.Revoke("alpha");
    LinkAddress remaining = Assert.Single(registry.Snapshot);
    Assert.Equal("beta", remaining.Name);
  }
}
