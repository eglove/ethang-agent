using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>
/// Seam for steering an active turn: the human posts messages while the agent loop is
/// running, and the loop drains them at safe points — never between an assistant tool-call
/// message and its tool results, so protocol ordering stays valid. Drained messages enter
/// the conversation as User messages and are seen by the model on its next provider call.
/// Implementations must be thread-safe: Post is called from the UI surface, TryTake from
/// the loop's worker flow.
/// </summary>
public interface IAgentInbox
{
  /// <summary>Queues a steering message for delivery at the next safe point. Null, empty,
  ///     or whitespace text is rejected.</summary>
  void Post(string text);

  /// <summary>Takes the oldest queued message, or returns false when the inbox is empty.</summary>
  bool TryTake(out string text);
}

/// <summary>The bounded, per-agent mailbox: one instance per agent. Generalizes the
///     roots-only steering inbox (source step 4) — every agent owns one; the runtime's
///     Deliver path and the loop's drain path meet here. Overflow FAILS THE SENDER with
///     MailboxFull (P3); the legacy <see cref="IAgentInbox.Post"/> void shape discards
///     overflow at that seam because the human caller has no result channel.
///     Between-turn durability is the mailbox store's job, not this class's.
///     Thread-safe.</summary>
public sealed class BoundedAgentMailbox : IAgentInbox, IAgentMailbox
{
  public const int DefaultCapacity = 64;

  private readonly Lock _gate = new();
  private readonly Queue<PendingMessage> _pending = [];
  private readonly int _capacity;

  /// <summary>False once the owning run has settled: Deliver then fails NotRunning.
  ///     Flipped by the runtime; the loop drain path ignores it.</summary>
  private volatile bool _accepting = true;

  public BoundedAgentMailbox(int capacity = DefaultCapacity)
  {
    if (capacity < 1)
    {
      throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be at least 1.");
    }

    _capacity = capacity;
  }

  public int UnreadCount
  {
    get
    {
      lock (_gate)
      {
        return _pending.Count;
      }
    }
  }

  /// <summary>Addressable delivery: fails MailboxFull / NotRunning to the sender.</summary>
  public Result<bool> Deliver(PendingMessage message)
  {
    ArgumentNullException.ThrowIfNull(message);
    if (string.IsNullOrWhiteSpace(message.Text))
    {
      return Result.Failure<bool>(new DomainError("InvalidMessage", "message text must not be empty."));
    }

    lock (_gate)
    {
      if (!_accepting)
      {
        return Result.Failure<bool>(new DomainError(MailboxErrors.NotRunning, $"agent is not running; message refused."));
      }

      if (_pending.Count >= _capacity)
      {
        return Result.Failure<bool>(new DomainError(MailboxErrors.Full,
            $"mailbox is at capacity {_capacity}; batch or wait before sending again."));
      }

      _pending.Enqueue(message);
      return Result.Success(true);
    }
  }

  /// <summary>Closes the delivery side when the owning run settles. Idempotent.</summary>
  public void Close()
  {
    lock (_gate)
    {
      _accepting = false;
    }
  }

  /// <inheritdoc cref="IAgentInbox.Post"/>
  public void Post(string text)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(text);
    _ = Deliver(new PendingMessage(text, MessageUrgency.Normal, DateTimeOffset.UtcNow, "human"));
  }

  /// <inheritdoc cref="IAgentInbox.TryTake"/>
  public bool TryTake(out string text)
  {
    lock (_gate)
    {
      if (_pending.Count == 0)
      {
        text = "";
        return false;
      }

      PendingMessage message = _pending.Dequeue();
      text = message.Text;
      return true;
    }
  }

  /// <inheritdoc cref="IAgentMailbox.Drain"/>
  public IReadOnlyList<PendingMessage> Drain()
  {
    lock (_gate)
    {
      IReadOnlyList<PendingMessage> drained = [.. _pending];
      _pending.Clear();
      return drained;
    }
  }
}
