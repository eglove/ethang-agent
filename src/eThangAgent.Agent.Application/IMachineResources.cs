using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>Machine memory seam (spec D1): the governor derives its budget from the
///     machine the harness runs on, never from a constant. A failed probe is a typed
///     failure - a fabricated budget would silently mis-size every threshold.</summary>
public interface IMachineResources
{
  /// <summary>Total physical memory the process may use, in bytes. On a
  ///     container-constrained host this is the container's limit, which is the
  ///     number that actually governs us.</summary>
  Result<long> TotalPhysicalBytes();
}
