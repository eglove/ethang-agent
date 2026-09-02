using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Transport.ACL;

/// <summary>The addressable side of a REMOTE child's mailbox, as seen from another
///     container in the app process (W3.2): Deliver forwards over the wire to the host's
///     mailbox enqueue (FR-X3's at-least-once path; receipts return to the sender
///     unchanged). Drain and UnreadCount are owner-side concepts and deliberately
///     non-meaningful across the wire — Drain is owner-only by contract and is never
///     reached through this proxy (the capability provider's cross-container half only
///     delivers), and the unread count lives with the host's own mailbox (W4.4 reads it
///     host-side). Named decision: Drain returns an empty list instead of throwing so
///     the proxy stays a plain data-shape implementation of the seam; a caller
///     violating the owner-only rule gets silence, not a fault, exactly as the
///     stale-deliver path does.</summary>
public sealed class RemoteMailboxProxy(IAgentRuntime remote, AgentId id) : IAgentMailbox
{
  private readonly IAgentRuntime _remote = remote ?? throw new ArgumentNullException(nameof(remote));

  /// <inheritdoc cref="IAgentMailbox.Deliver"/>
  public Result<bool> Deliver(PendingMessage message) => _remote.Deliver(id, message);

  /// <summary>Owner-only by contract; never proxied over the wire (see type summary).</summary>
  public IReadOnlyList<PendingMessage> Drain() => [];

  /// <summary>Unknown across the wire; the host's mailbox owns the true count.</summary>
  public int UnreadCount => 0;
}
