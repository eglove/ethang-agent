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
    /// or whitespace text is rejected.</summary>
    void Post(string text);

    /// <summary>Takes the oldest queued message, or returns false when the inbox is empty.</summary>
    bool TryTake(out string text);
}

/// <summary>Default lock-guarded inbox. One instance per agent session.</summary>
public sealed class AgentInbox : IAgentInbox
{
    private readonly object _gate = new();
    private readonly Queue<string> _pending = [];

    public void Post(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        lock (_gate)
            _pending.Enqueue(text);
    }

    public bool TryTake(out string text)
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                text = "";
                return false;
            }
            text = _pending.Dequeue();
            return true;
        }
    }
}