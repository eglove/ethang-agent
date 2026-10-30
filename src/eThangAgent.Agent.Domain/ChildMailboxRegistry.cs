namespace eThangAgent.AgentDomain;

/// <summary>Per-session registry of live child mailboxes: the child loop resolves its own
///     inbox here, so composition can wire the spawner's inbox source without resolving the
///     runtime itself (which is registered AS IAgentRuntime and may not exist concretely in
///     remote mode). Thread-safe.
///     <para>THE SENDER-SIDE LOOKUP IS DELETED (issue #316), the loop's side is KEPT. Nothing
///     pushes into a child's mailbox any more - agent-to-agent messaging is gone - but the
///     child loop still takes its inbox from here at start, and the issue's scope note is
///     explicit that this issue must not remove a mailbox a future per-agent steering channel
///     would need. So the registry stays, one accessor narrower.</para></summary>
public sealed class ChildMailboxRegistry
{
  private readonly Lock _gate = new();
  private readonly Dictionary<Guid, IAgentMailbox> _mailboxes = [];

  /// <summary>Registers (or replaces) the mailbox for one agent id.</summary>
  public void Register(AgentId id, IAgentMailbox mailbox)
  {
    lock (_gate)
    {
      _mailboxes[id.Value] = mailbox;
    }
  }

  /// <summary>Unregisters an id (run settled; the box is closed and drained first).</summary>
  public void Unregister(AgentId id)
  {
    lock (_gate)
    {
      _ = _mailboxes.Remove(id.Value);
    }
  }

  /// <summary>The live mailbox for an id, or null (never started, settled, or foreign).</summary>
  public IAgentInbox? InboxFor(AgentId id)
  {
    lock (_gate)
    {
      return _mailboxes.TryGetValue(id.Value, out IAgentMailbox? mailbox) ? mailbox as IAgentInbox : null;
    }
  }
}
