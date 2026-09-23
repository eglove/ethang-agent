using System.Collections.Concurrent;
using eThangAgent.ToolDomain;

namespace eThangAgent.ComputerUse.ACL;

/// <summary>Process-wide computer access provider (spec 1.3/A4): one BrokerSupervisor
///     per workspace root (shared by every session in it), yielding ONE shared
///     BrokerComputerAccess per root (fix round 5, F4) - a per-call instance would
///     give each session its own ObservationLedger/FrameRegistry/lease over one
///     connection, defeating the ledger fail-closed contracts. The cache is
///     process-lifetime and thread-safe; the access instances are shared, so
///     disposal stays the composition's concern (DisposeAsync is a no-op when the
///     access does not own its supervisor). Registered by net10.0-windows hosts at
///     the PROCESS level; hosts without the ACL never enable the tool (and never
///     construct this provider).</summary>
public sealed class BrokerComputerAccessProvider(BrokerRegistry registry) : IComputerAccessProvider
{
  private readonly BrokerRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
  private readonly ConcurrentDictionary<string, BrokerComputerAccess> _accessByRoot = new(StringComparer.OrdinalIgnoreCase);

  /// <inheritdoc />
  public IComputerAccess? ForWorkspace(string workspaceRoot)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
    return _accessByRoot.GetOrAdd(workspaceRoot, root => new BrokerComputerAccess(_registry.GetOrCreate(root)));
  }
}
