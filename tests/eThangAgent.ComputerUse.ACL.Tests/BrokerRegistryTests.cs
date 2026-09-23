namespace eThangAgent.ComputerUse.ACL.Tests;

/// <summary>Registry identity (the brief's task-13 contract): same supervisor per workspace
///     root, different across roots, thread-safe GetOrCreate.</summary>
public class BrokerRegistryTests
{
  [Fact]
  public void GetOrCreate_ReturnsSameInstancePerRoot_AndDifferentAcrossRoots()
  {
    string stubHost = StubHostBuilder.Build();
    BrokerRegistry registry = new(() => stubHost, root => "ethang-test-" + BrokerRegistry.StableWorkspaceId(root));
    BrokerSupervisor first = registry.GetOrCreate(@"C:\w\alpha");
    BrokerSupervisor second = registry.GetOrCreate(@"C:\w\alpha");
    BrokerSupervisor other = registry.GetOrCreate(@"C:\w\beta");
    Assert.Same(first, second);
    Assert.NotSame(first, other);
  }

  [Fact]
  public void StableWorkspaceId_IsStablePerRoot_AndDistinctAcrossRoots()
  {
    string a1 = BrokerRegistry.StableWorkspaceId(@"C:\w\alpha");
    string a2 = BrokerRegistry.StableWorkspaceId(@"C:\w\alpha");
    string b = BrokerRegistry.StableWorkspaceId(@"C:\w\beta");
    Assert.Equal(a1, a2);
    Assert.NotEqual(a1, b);
    Assert.Matches("^[0-9A-F]{64}$", a1);
  }
}
