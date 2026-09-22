using eThangAgent.ToolDomain;

namespace eThangAgent.ComputerUse.ACL;

/// <summary>Process-wide computer access provider (spec 1.3/A4): one BrokerSupervisor per
///     workspace root (shared by every session in it), each yielding a BrokerComputerAccess.
///     Registered by net10.0-windows hosts at composition; hosts without the ACL keep the
///     NullComputerAccess fallback.</summary>
public sealed class BrokerComputerAccessProvider(BrokerRegistry registry) : IComputerAccessProvider
{
  private readonly BrokerRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));

  /// <inheritdoc />
  public IComputerAccess? ForWorkspace(string workspaceRoot) =>
      new BrokerComputerAccess(_registry.GetOrCreate(workspaceRoot));
}
