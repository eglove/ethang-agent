using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Decorates the root steering inbox so every accepted post also wakes any
///     active agent.wait on this session (the unconditional user wake, spec decisions
///     1+2). The wake fires AFTER the inner inbox accepted the message, so a rejected
///     post wakes nobody. The wake is STICKY: it stays armed while a message remains
///     pending, so a wait that starts after the post still wakes for it — the user
///     message that lands while the parent is between safe points (inside a spawn
///     exec, mid-provider call) sits in the inbox, and the ephemeral no-replay wake
///     would lose exactly that signal. The sticky wake clears once the queue is empty:
///     a delivered message must not wake a later wait spuriously. The wake fires
///     regardless of any waiter's wakeOn filter: the filter is applied by the wait
///     logic, and UserMessage always passes it.
///     <para>IT FORWARDS THE WHOLE CONTRACT IT WRAPS (issue #319 D2). It used to
///     implement only <see cref="IAgentInbox"/>, and that narrowing WAS the pinned-echo
///     defect: the host delivers a steering message through
///     <see cref="IAgentMailbox.Deliver"/> when its root inbox is addressable, so a
///     decorator that was not addressable sent every steer down the legacy
///     <see cref="IAgentInbox.TryPost"/> path — which MINTS A FRESH IDENTITY — and the
///     loop's drain fact then named an id nothing in the app had pinned an echo under.
///     The echo stayed pinned for the rest of the session. The inner inbox must
///     therefore be addressable: a decorator that cannot forward <c>Deliver</c> cannot
///     preserve the sender's identity, and refusing at construction is the honest
///     failure rather than a silent drop.</para>
///     <para>The sticky wake is driven by the INNER box's own unread count rather than
///     a parallel counter kept here. One source of truth: a count maintained beside the
///     queue could disagree with it the moment a second delivery path (<c>Deliver</c>)
///     started enqueueing, which is exactly what this issue added.</para></summary>
public sealed class SignalingInbox(IAgentInbox inner, WaitWakeHub hub, AgentId ownerId)
    : IAgentInbox, IAgentMailbox
{
  private readonly IAgentInbox _inner = inner ?? throw new ArgumentNullException(nameof(inner));
  private readonly WaitWakeHub _hub = hub ?? throw new ArgumentNullException(nameof(hub));
  private readonly AgentId _ownerId = ownerId;

  // The addressable view of the same box. Required, not optional: see the class summary.
  private readonly IAgentMailbox _mailbox = inner as IAgentMailbox ?? throw new ArgumentException(
      "The decorated inbox must be addressable (IAgentMailbox): this decorator forwards "
      + "Deliver so the sender's message identity survives (issue #319 D2).",
      nameof(inner));

  /// <summary>Posts through the inner inbox and arms the sticky wake ONLY when the
  ///     inner inbox accepted the message (issue #189 D2): a refused post (full or
  ///     closed mailbox) wakes nobody and leaves no pending message behind. The
  ///     decorator's own contract - "a rejected post wakes nobody" - now holds.</summary>
  public Result<bool> TryPost(string text)
  {
    ArgumentNullException.ThrowIfNull(text);
    return Accepted(_inner.TryPost(text));
  }

  /// <summary>The void wrapper: loop-facing callers keep the legacy shape.</summary>
  public void Post(string text) => _ = TryPost(text);

  /// <summary>Addressable delivery, forwarded with the sender's identity intact (issue
  ///     #319 D1/D2). This is the path the host's root steering takes, so the id the app
  ///     pinned its echo under is the id the loop's drain fact names back.</summary>
  public Result<bool> Deliver(PendingMessage message)
  {
    ArgumentNullException.ThrowIfNull(message);
    return Accepted(_mailbox.Deliver(message));
  }

  /// <inheritdoc cref="IAgentInbox.TryTake"/>
  public bool TryTake(out string text, out Guid messageId)
  {
    bool taken = _inner.TryTake(out text, out messageId);
    if (taken)
    {
      ClearStickyWhenEmpty();
    }

    return taken;
  }

  /// <inheritdoc cref="IAgentMailbox.Drain"/>
  public IReadOnlyList<PendingMessage> Drain()
  {
    IReadOnlyList<PendingMessage> drained = _mailbox.Drain();
    ClearStickyWhenEmpty();
    return drained;
  }

  /// <inheritdoc cref="IAgentMailbox.UnreadCount"/>
  public int UnreadCount => _mailbox.UnreadCount;

  /// <summary>Arms the sticky user wake when the inner box took the message. The wake
  ///     fires AFTER acceptance, so a refusal wakes nobody.</summary>
  private Result<bool> Accepted(Result<bool> accepted)
  {
    if (accepted.IsSuccess)
    {
      _hub.RegisterSticky(new WakeSignal(WakeReason.UserMessage, _ownerId, DateTimeOffset.UtcNow,
          "user message waiting"));
    }

    return accepted;
  }

  /// <summary>Clears the sticky wake once nothing is left queued: a delivered message must
  ///     not wake a later wait spuriously. Read from the INNER box, so the two can never
  ///     disagree about what is pending.</summary>
  private void ClearStickyWhenEmpty()
  {
    if (_mailbox.UnreadCount == 0)
    {
      _hub.ClearSticky(WakeReason.UserMessage);
    }
  }
}
