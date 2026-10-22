using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Decorates the root steering inbox so every accepted post also wakes any
///     active agent.wait on this session (the unconditional user wake, spec decisions
///     1+2). The wake fires AFTER the inner inbox accepted the message, so a rejected
///     post wakes nobody. The wake is STICKY: it stays armed while a message remains
///     pending, so a wait that starts after the post still wakes for it — the user
///     message that lands while the parent is between safe points (inside a spawn
///     exec, mid-provider call) sits in the inbox, and the ephemeral no-replay wake
///     would lose exactly that signal. TryTake clears the sticky wake once the queue
///     is empty: a delivered message must not wake a later wait spuriously. The wake
///     fires regardless of any waiter's wakeOn filter: the filter is applied by the
///     wait logic, and UserMessage always passes it.</summary>
public sealed class SignalingInbox(IAgentInbox inner, WaitWakeHub hub, AgentId ownerId) : IAgentInbox
{
  private readonly IAgentInbox _inner = inner ?? throw new ArgumentNullException(nameof(inner));
  private readonly WaitWakeHub _hub = hub ?? throw new ArgumentNullException(nameof(hub));
  private readonly AgentId _ownerId = ownerId;
  // Pending count tracked here (the inner inbox exposes no peek): the sticky wake
  // clears exactly when the count reaches zero — the queue is drained.
  private int _pending;

  /// <summary>Posts through the inner inbox and arms the sticky wake ONLY when the
  ///     inner inbox accepted the message (issue #189 D2): a refused post (full or
  ///     closed mailbox) wakes nobody and leaves no pending count behind. The
  ///     decorator's own contract - "a rejected post wakes nobody" - now holds.</summary>
  public Result<bool> TryPost(string text)
  {
    ArgumentNullException.ThrowIfNull(text);
    Result<bool> accepted = _inner.TryPost(text);
    if (accepted.IsSuccess)
    {
      _ = Interlocked.Increment(ref _pending);
      _hub.RegisterSticky(new WakeSignal(WakeReason.UserMessage, _ownerId, DateTimeOffset.UtcNow,
          "user message waiting"));
    }

    return accepted;
  }

  /// <summary>The void wrapper: loop-facing callers keep the legacy shape.</summary>
  public void Post(string text) => _ = TryPost(text);

  public bool TryTake(out string text)
  {
    bool taken = _inner.TryTake(out text!);
    if (taken && Interlocked.Decrement(ref _pending) <= 0)
    {
      _hub.ClearSticky(WakeReason.UserMessage);
    }

    return taken;
  }
}
