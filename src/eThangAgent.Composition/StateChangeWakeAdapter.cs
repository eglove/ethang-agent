using eThangAgent.AgentDomain;
using eThangAgent.StateDomain;

namespace eThangAgent.Composition;

/// <summary>Adapts the State Domain's state-change bus onto the Agent Domain's wake
///     hub (the State Domain must not reference the Agent Domain, so the composition
///     owns the translation). A state write has no child id: the signal carries
///     default(AgentId); the wait's filter matches on reason only, and the digest
///     line renders the key and kind.</summary>
public sealed class StateChangeWakeAdapter(IStateChangeBus bus, WaitWakeHub hub) : IDisposable
{
  private readonly IDisposable _lease = bus.Subscribe(change => hub.Register(new WakeSignal(
      WakeReason.StateWrite,
      default,
      change.At,
      $"state {change.Key} {change.Kind}")));

  public void Dispose() => _lease.Dispose();
}
