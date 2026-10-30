using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>One queued mailbox message. Metadata (sender, arrival) rides beside the text;
///     content persists only with the receiver's transcript on drain (FR-C4/D5).
///     <para><paramref name="Id"/> is the message's identity, and it is REQUIRED (issue #92):
///     the delivery handshake works by naming a message, so a message without one could
///     never be asked about. It is minted by the sender - see
///     <see cref="New(string, string, DateTimeOffset?)"/> - and travels with the message
///     through the mailbox and the wire, so the drain fact can name exactly which messages
///     it consumed.</para>
///     <para>THE URGENCY CLASS IS GONE (issue #316). Normal/Attention/Urgent existed so one
///     agent could ask another to drain sooner or, with an audited grant, interrupt its turn
///     outright. With agent-to-agent messaging retired the only sender is the human steering
///     a root turn, and a person typing into a live turn has no urgency to declare - it is
///     always the same message. Keeping a three-value ladder that only ever carries one
///     value would advertise an interrupt capability nothing can exercise.</para></summary>
public sealed record PendingMessage(string Text, DateTimeOffset At, string Sender, Guid Id)
{
  /// <summary>Mints a message with a fresh identity - the shape every sender uses. The
  ///     explicit constructor stays available for replays that must preserve an identity
  ///     (a wire re-delivery).</summary>
  public static PendingMessage New(string text, string sender, DateTimeOffset? at = null)
      => new(text, at ?? DateTimeOffset.UtcNow, sender, Guid.NewGuid());
}

/// <summary>Addressable side of one agent's mailbox. Deliver FAILS THE SENDER on full or
///     not-running (P3: errors are information, never silent drops); Drain is called only
///     by the owner at its safe points. Implementations must be thread-safe.
///     Delivery is always pushed by the caller - never discovered by polling (A1).</summary>
public interface IAgentMailbox
{
  /// <summary>Enqueues a message. Fails MailboxFull when at capacity, NotRunning when the
  ///     owning agent has settled. True on success - the receipt carries no payload.</summary>
  Result<bool> Deliver(PendingMessage message);

  /// <summary>Takes every queued message in arrival order and empties the box.
  ///     Owner-only, safe points only.</summary>
  IReadOnlyList<PendingMessage> Drain();

  /// <summary>Messages currently queued; hosts surface this as the unread count.</summary>
  int UnreadCount { get; }
}

/// <summary>Canonical mailbox error codes surfaced to senders as tool results.</summary>
public static class MailboxErrors
{
  public const string Full = "MailboxFull";
  public const string NotRunning = "NotRunning";
}
