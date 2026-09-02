using System.Collections.Concurrent;
using eThangAgent.AgentDomain;

namespace eThangAgent.AgentInfrastructure;

/// <summary>Process-wide cross-container mailbox locator (W3.2): one instance is shared
///     by every session container a host opens, and each container contributes ONE source
///     — a function answering for the live mailboxes IT owns (its runtime's registry).
///     TryGet consults the sources in registration order and returns the first live
///     mailbox for the id, so agent.route's fallback delivers into the OWNING session's
///     child mailbox. Same-process scope by contract: an id no registered container holds
///     resolves to none — another app instance stays unreachable and honestly reports
///     NotRunning. Thread-safe: sources are copied on each probe (registration is rare,
///     probing is per-route).</summary>
public sealed class ProcessMailboxLocator : IAgentMailboxLocator
{
  private readonly ConcurrentQueue<MailboxSource> _sources = new();

  private sealed record MailboxSource(Func<AgentId, IAgentMailbox?> Resolve, IAgentEvents? Events);

  /// <summary>The number of containers currently contributing sources.</summary>
  public int SourceCount => _sources.Count;

  /// <summary>Registers one container's live-mailbox view (its ChildMailboxRegistry's
  ///     MailboxFor, or a test fake) and, optionally, the event stream deliveries into
  ///     that container publish on. Order is registration order; duplicates are the
  ///     caller's wiring fault and harmless in practice (a container consulted twice
  ///     answers the same mailbox).</summary>
  public void AddSource(Func<AgentId, IAgentMailbox?> source, IAgentEvents? events = null)
  {
    ArgumentNullException.ThrowIfNull(source);
    _sources.Enqueue(new MailboxSource(source, events));
  }

  /// <inheritdoc cref="IAgentMailboxLocator.TryGet"/>
  public IAgentMailbox? TryGet(AgentId id)
      => _sources.Select(s => s.Resolve(id)).FirstOrDefault(m => m is not null);

  /// <summary>The event stream of the container OWNING <paramref name="id"/>'s mailbox,
  ///     or null — the target-side audit surface (W3.3): the capability provider asks
  ///     here after a successful cross-container delivery to publish its
  ///     MessageDelivered(cross-container) event where the target's host listens.</summary>
  public IAgentEvents? EventsFor(AgentId id)
      => _sources.Where(s => s.Resolve(id) is not null)
          .Select(s => s.Events)
          .FirstOrDefault();
}
