using eThangAgent.Agent.Application;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentInfrastructure;

/// <summary>Machine memory from the runtime's own view (spec D1): the GC's reported
///     available memory already accounts for container and job limits, so it is the
///     number that governs allocation here. Never a constant, never a guess.</summary>
public sealed class MachineResources : IMachineResources
{
  public Result<long> TotalPhysicalBytes()
  {
    long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
    return available > 0
        ? Result.Success(available)
        : Result.Failure<long>(new DomainError("MachineMemoryUnavailable",
            "The runtime reported no available memory for this machine."));
  }
}
