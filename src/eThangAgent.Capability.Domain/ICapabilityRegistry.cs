using eThangAgent.SharedKernel;

namespace eThangAgent.CapabilityDomain;

public interface ICapabilityRegistry
{
  Result<ResolvedCapability> Resolve(string nameOrRef);

  IReadOnlyList<ProviderCapabilities> Providers { get; }

  Task<CapabilityInvocationResult> InvokeAsync(
      ResolvedCapability capability, string jsonArguments, CancellationToken ct = default);
}
