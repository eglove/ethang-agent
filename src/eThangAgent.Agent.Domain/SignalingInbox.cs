namespace eThangAgent.AgentDomain;

/// <summary>Decorates the root steering inbox so every accepted post also wakes any
///     active agent.wait on this session (the unconditional user wake, spec decisions
///     1+2). The wake fires AFTER the inner inbox accepted the message, so a rejected
///     post wakes nobody. TryTake is a pure forward — drain semantics stay the loop's.
///     The wake fires regardless of any waiter's wakeOn filter: the filter is applied
///     by the wait logic, and UserMessage always passes it.</summary>
public sealed class SignalingInbox(IAgentInbox inner, WaitWakeHub hub, AgentId ownerId) : IAgentInbox
{
  private readonly IAgentInbox _inner = inner ?? throw new ArgumentNullException(nameof(inner));
  private readonly WaitWakeHub _hub = hub ?? throw new ArgumentNullException(nameof(hub));
  private readonly AgentId _ownerId = ownerId;

  public void Post(string text)
  {
    ArgumentNullException.ThrowIfNull(text);
    _inner.Post(text);
    _hub.Register(new WakeSignal(WakeReason.UserMessage, _ownerId, DateTimeOffset.UtcNow,
        "user message waiting"));
  }

  public bool TryTake(out string text) => _inner.TryTake(out text!);
}
