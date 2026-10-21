using eThangAgent.Agent.Application;
using eThangAgent.AgentInfrastructure;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Infrastructure.Tests;

public sealed class MachineResourcesTests
{
  [Fact]
  public void MachineResources_ImplementsTheSeam() =>
    Assert.True(typeof(IMachineResources).IsAssignableFrom(typeof(MachineResources)));

  [Fact]
  public void TotalPhysicalBytes_OnThisMachine_ReturnsPositiveValue()
  {
    MachineResources resources = new();

    Result<long> result = resources.TotalPhysicalBytes();

    Assert.True(result.IsSuccess);
    Assert.True(result.Value > 0);
  }

  [Fact]
  public void TotalPhysicalBytes_OnThisMachine_ExceedsOneGigabyte()
  {
    MachineResources resources = new();

    Result<long> result = resources.TotalPhysicalBytes();

    Assert.True(result.Value > 1_000_000_000L);
  }
}
