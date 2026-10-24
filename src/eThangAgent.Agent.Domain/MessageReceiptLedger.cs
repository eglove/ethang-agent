namespace eThangAgent.AgentDomain;

/// <summary>What became of one steering message, as far as the sender can know (issue #92).
///     <para><see cref="Queued"/>: the message landed in the receiver's mailbox and the
///     receiver has not drained it yet. <see cref="Received"/>: the owning loop drained it
///     at a safe point and appended it to its conversation — the model WILL see it on its
///     next provider call. <see cref="Undelivered"/>: the run settled with the message
///     still queued, so it was persisted for the next start and the model never saw it in
///     that run.</para>
///     <para>The three states are facts observed from the mailbox lifecycle, never
///     inferences: no state is ever guessed from elapsed time or from the receiver's
///     status.</para></summary>
public enum MessageReceiptState
{
  Queued = 0,
  Received = 1,
  Undelivered = 2,
}

/// <summary>One message's delivery fact. Metadata only — the text stays in the mailbox
///     and the transcript (D5), never here.</summary>
public sealed record MessageReceipt(AgentId ChildId, Guid MessageId, MessageReceiptState State, DateTimeOffset At);

/// <summary>The session's message receipt ledger (issue #92): the read surface that turns
///     the mailbox lifecycle events into per-message delivery facts, so a sender can tell
///     a message the child RECEIVED from one still queued and from one the run settled
///     without draining.
///
///     <para>Before this, agent.send's receipt said "delivered" and meant only "enqueued":
///     the parent could not distinguish a message the child's loop had actually consumed
///     from one sitting in the mailbox, and a message persisted as an undelivered
///     remainder at settle was indistinguishable from a received one. The emission side
///     already carried the facts — <see cref="MessageDeliveredEvent"/> at enqueue,
///     <see cref="MailboxDrainedEvent"/> at the loop's safe point, and now
///     <see cref="MailboxUndeliveredEvent"/> at settle — but nothing joined them per
///     message.</para>
///
///     <para>Like the progress ledger (issue #93) this is a SUBSCRIBER, not a store: it
///     keeps at most <c>capacity</c> receipts in memory, oldest evicted first, and it never
///     writes to disk. It creates a receipt ONLY for a message it saw delivered: a drain
///     or remainder fact naming an id it never saw delivered is ignored, so the ledger can
///     never report a state for a message that was never enqueued. Subscriber faults are
///     contained by the stream itself; this type only reads event fields.</para></summary>
public sealed class MessageReceiptLedger : IAgentEventSubscriber
{
  /// <summary>Default bound on remembered receipts. A session's steering traffic is small
  ///     (a mailbox holds 64 messages and a parent steers a handful of times per run), so
  ///     this covers many runs while keeping the ledger O(1) in memory.</summary>
  public const int DefaultCapacity = 512;

  private readonly Lock _gate = new();
  private readonly int _capacity;
  // Insertion order is the delivery order — the order a digest renders in. The index
  // makes Find O(1); eviction pops the front of the list.
  private readonly List<MessageReceipt> _ordered = [];
  private readonly Dictionary<Guid, MessageReceipt> _byMessage = [];

  public MessageReceiptLedger(int capacity = DefaultCapacity)
  {
    if (capacity < 1)
    {
      throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be at least 1.");
    }

    _capacity = capacity;
  }

  /// <inheritdoc cref="IAgentEventSubscriber.OnEvent"/>
  public void OnEvent(ChildEvent evt)
  {
    ArgumentNullException.ThrowIfNull(evt);
    lock (_gate)
    {
      switch (evt)
      {
        case MessageDeliveredEvent delivered:
          Record(delivered.ChildId, delivered.MessageId, MessageReceiptState.Queued, delivered.At);
          break;
        case MailboxDrainedEvent drained:
          foreach (Guid id in drained.MessageIds)
          {
            Advance(drained.ChildId, id, MessageReceiptState.Received, drained.At);
          }

          break;
        case MailboxUndeliveredEvent undelivered:
          foreach (Guid id in undelivered.MessageIds)
          {
            Advance(undelivered.ChildId, id, MessageReceiptState.Undelivered, undelivered.At);
          }

          break;
        default:
          break; // only mailbox-lifecycle facts belong to this ledger
      }
    }
  }

  /// <summary>One message's fact, or null when this ledger never saw it delivered (or has
  ///     since evicted it).</summary>
  public MessageReceipt? Find(AgentId childId, Guid messageId)
  {
    lock (_gate)
    {
      return _byMessage.TryGetValue(messageId, out MessageReceipt? receipt)
          && receipt.ChildId.Equals(childId) ? receipt : null;
    }
  }

  /// <summary>Every receipt this ledger holds for one child, in delivery order. Empty when
  ///     the child has none (never steered here, or evicted).</summary>
  public IReadOnlyList<MessageReceipt> ForChild(AgentId childId)
  {
    lock (_gate)
    {
      return [.. _ordered.Where(r => r.ChildId.Equals(childId))];
    }
  }

  /// <summary>Records a delivery. A re-delivery of the same id (an at-least-once wire
  ///     replay) keeps the FIRST fact: identity is the message, and a replay carries no new
  ///     information. A message with no identity (Guid.Empty — a legacy publisher that
  ///     predates the handshake) is not recorded at all, because a receipt keyed on an
  ///     unknown id could never be asked about.</summary>
  private void Record(AgentId childId, Guid messageId, MessageReceiptState state, DateTimeOffset at)
  {
    if (messageId == Guid.Empty || _byMessage.ContainsKey(messageId))
    {
      return;
    }

    MessageReceipt receipt = new(childId, messageId, state, at);
    _byMessage[messageId] = receipt;
    _ordered.Add(receipt);
    while (_ordered.Count > _capacity)
    {
      _ = _byMessage.Remove(_ordered[0].MessageId);
      _ordered.RemoveAt(0);
    }
  }

  /// <summary>Moves a known message to a later state. An id this ledger never saw
  ///     delivered is IGNORED: a drain or remainder fact can name a message from a
  ///     previous session's mailbox, and inventing a receipt for it would report a
  ///     delivery that never happened here. Received never regresses to Undelivered —
  ///     a drained message reached the conversation, and a later settle cannot un-deliver
  ///     it.</summary>
  private void Advance(AgentId childId, Guid messageId, MessageReceiptState state, DateTimeOffset at)
  {
    if (!_byMessage.TryGetValue(messageId, out MessageReceipt? current) || !current.ChildId.Equals(childId))
    {
      return;
    }

    if (current.State is MessageReceiptState.Received)
    {
      return;
    }

    MessageReceipt updated = current with { State = state, At = at };
    _byMessage[messageId] = updated;
    int index = _ordered.IndexOf(current);
    if (index >= 0)
    {
      _ordered[index] = updated;
    }
  }
}
