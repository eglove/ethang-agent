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
  public void Revoke_UnknownName_FailsNotFound()
  {
    AgentLinkRegistry registry = new();
    Result<bool> revoked = registry.Revoke("ghost");
    Assert.False(revoked.IsSuccess);
    Assert.Equal("NotFound", revoked.Error.Code);
  }
}
