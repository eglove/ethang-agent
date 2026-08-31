using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Message urgency classes (FR-C9). Normal drains at iteration boundaries;
///     Attention requests a drain at the next tool boundary; Urgent may preempt the
///     receiver's turn — but only under an explicit, audited contract grant (D4-revised).</summary>
public enum MessageUrgency
{
  Normal = 0,
  Attention = 1,
  Urgent = 2,
}

/// <summary>One queued mailbox message. Metadata (sender, urgency, arrival) rides beside
///     the text; content persists only with the receiver's transcript on drain (FR-C4/D5).</summary>
public sealed record PendingMessage(string Text, MessageUrgency Urgency, DateTimeOffset At, string Sender);

/// <summary>Addressable side of one agent's mailbox. Deliver FAILS THE SENDER on full or
///     not-running (P3: errors are information, never silent drops); Drain is called only
///     by the owner at its safe points. Implementations must be thread-safe.
///     Delivery is always pushed by the runtime — never discovered by polling (A1).</summary>
public interface IAgentMailbox
{
  /// <summary>Enqueues a message. Fails MailboxFull when at capacity, NotRunning when the
  ///     owning agent has settled. True on success — the receipt carries no payload.</summary>
  Result<bool> Deliver(PendingMessage message);

  /// <summary>Takes every queued message in arrival order (per-sender FIFO under the
  ///     global bound) and empties the box. Owner-only, safe points only.</summary>
  IReadOnlyList<PendingMessage> Drain();

  /// <summary>Messages currently queued; hosts surface this as the unread count.</summary>
  int UnreadCount { get; }
}

/// <summary>Canonical mailbox error codes surfaced to senders as tool results.</summary>
public static class MailboxErrors
{
  public const string Full = "MailboxFull";
  public const string NotRunning = "NotRunning";
  public const string UrgencyNotGranted = "UrgencyNotGranted";
}
