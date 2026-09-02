namespace eThangAgent.AgentDomain;

/// <summary>Cross-container mailbox discovery (W3): resolves an agent id to the LIVE
///     mailbox of a container other than the caller's own — the second half of
///     agent.route's delivery path, consulted when (and only when) the local runtime
///     fails NotRunning. An absent locator keeps today's single-container behavior
///     byte-for-byte. Same-process scope by contract: an id no live container in this
///     process holds resolves to none — a second app instance stays out of reach and
///     its agents honestly report NotRunning. Implementations must be thread-safe:
///     routing runs on any agent's turn thread.</summary>
public interface IAgentMailboxLocator
{
  /// <summary>The live mailbox for <paramref name="id"/> in another container, or null
  ///     (unknown, settled, or out of process). The returned mailbox is addressable-side
  ///     only: Deliver fails the sender (MailboxFull / NotRunning) exactly as the local
  ///     runtime's does; Drain remains the owning loop's and is never reached through
  ///     this seam.</summary>
  IAgentMailbox? TryGet(AgentId id);
}
