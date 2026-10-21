using eThangAgent.SharedKernel;

namespace eThangAgent.ConversationDomain;

public class Conversation(IEnumerable<Message>? seed = null)
{
  private readonly List<Message> _messages = [.. seed ?? []];
  private readonly List<Subscription> _subscriptions = [];

  private sealed class Subscription(Action<Message>? onAdded, Action? onReplaced)
  {
    public Action<Message>? _onAdded = onAdded;
    public Action? _onReplaced = onReplaced;
  }

  /// <summary>Observes conversation mutations until dispose: every Add* fires
  ///     <paramref name="onAdded"/> with the appended message; Compact fires
  ///     <paramref name="onReplaced"/> once per replacement. Null callbacks are
  ///     allowed. Disposing stops the events; disposing twice is harmless.</summary>
  public IDisposable Subscribe(Action<Message>? onAdded, Action? onReplaced = null)
  {
    Subscription subscription = new(onAdded, onReplaced);
    _subscriptions.Add(subscription);
    return new Unsubscriber(this, subscription);
  }

  private sealed class Unsubscriber(Conversation owner, Subscription subscription) : IDisposable
  {
    private bool _disposed;

    public void Dispose()
    {
      if (_disposed)
      {
        return;
      }

      _disposed = true;
      _ = owner._subscriptions.Remove(subscription);
    }
  }

  private void NotifyAdded(Message message)
  {
    foreach (Subscription s in _subscriptions.ToArray())
    {
      s._onAdded?.Invoke(message);
    }
  }

  private void NotifyReplaced()
  {
    foreach (Subscription s in _subscriptions.ToArray())
    {
      s._onReplaced?.Invoke();
    }
  }

  public IReadOnlyList<Message> Messages => _messages.AsReadOnly();

  /// <summary>Seeds the conversation with already-persisted history (hydration).
  ///     Deliberately NOT routed through the Add* methods: those raise the mutation
  ///     notification a transcript sink subscribes to, and a hydrated message is one the
  ///     store ALREADY holds — re-raising it would append every historical message a
  ///     second time. Callers seed before any sink is attached, which is why this is a
  ///     separate operation rather than a loop over Add*.</summary>
  public void Seed(IReadOnlyList<Message> history)
  {
    ArgumentNullException.ThrowIfNull(history);
    foreach (Message message in history)
    {
      _messages.Add(message);
    }
  }

  public void AddUserMessage(string text, IReadOnlyList<MessagePart>? parts = null)
  {
    Message message = new(Role.User, text, DateTimeOffset.UtcNow, Parts: NormalizeParts(Role.User, parts));
    _messages.Add(message);
    NotifyAdded(message);
  }

  public void AddAssistantMessage(string text, IReadOnlyList<MessagePart>? parts = null)
  {
    Message message = new(Role.Assistant, text, DateTimeOffset.UtcNow, Parts: NormalizeParts(Role.Assistant, parts));
    _messages.Add(message);
    NotifyAdded(message);
  }

  public void AddAssistantMessage(string text, IReadOnlyList<ToolCall>? toolCalls,
      IReadOnlyList<MessagePart>? parts = null)
  {
    Message message = new(Role.Assistant, text, DateTimeOffset.UtcNow, toolCalls,
        Parts: NormalizeParts(Role.Assistant, parts));
    _messages.Add(message);
    NotifyAdded(message);
  }

  public void AddToolResult(string toolCallId, string content)
      => AddToolResult(toolCallId, content, null);

  /// <summary>Appends a tool result; <paramref name="parts"/> may carry content parts
  ///     (e.g. a tool's screenshot) beside the text content.</summary>
  public void AddToolResult(string toolCallId, string content, IReadOnlyList<MessagePart>? parts)
  {
    Message message = new(Role.Tool, content, DateTimeOffset.UtcNow, ToolCallId: toolCallId,
        Parts: NormalizeParts(Role.Tool, parts));
    _messages.Add(message);
    NotifyAdded(message);
  }

  /// <summary>Appends a system-level message (e.g. a turn-boundary nudge). Null/whitespace is rejected.
  ///     Parts are rejected: only user and tool messages may carry them.</summary>
  public void AddSystemMessage(string text, IReadOnlyList<MessagePart>? parts = null)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(text);
    Message message = new(Role.System, text, DateTimeOffset.UtcNow, Parts: NormalizeParts(Role.System, parts));
    _messages.Add(message);
    NotifyAdded(message);
  }

  /// <summary>Normalizes the parts payload of a message under the aggregate invariant:
  ///     empty collapses to null (byte-identical to the legacy shape), and non-empty
  ///     parts on any role other than User or Tool are a programmer error.</summary>
  private static IReadOnlyList<MessagePart>? NormalizeParts(Role role, IReadOnlyList<MessagePart>? parts)
  {
    if (parts is not { Count: > 0 })
    {
      return null;
    }

    // A single conditional (not nested): the style rules want the throw folded into
    // the return while forbidding chained ternaries.
    return role is not Role.User and not Role.Tool
        ? throw new ArgumentException(
            $"Only user and tool messages may carry message parts; a {role} message cannot.", nameof(parts))
        : parts;
  }

  /// <summary>Replaces the whole message list (compaction). The aggregate enforces its
  ///     protocol invariants before mutating: the replacement is non-empty, every tool
  ///     result answers an earlier assistant tool call, and every assistant tool call
  ///     receives a later result — except the single trailing unanswered call, which is
  ///     the call currently executing (issue #222: a context-shrinking tool mutates the
  ///     conversation while its own call is still in flight, so refusing that one call
  ///     made every trim fail). Violations leave the conversation untouched.</summary>
  public Result<bool> Compact(IReadOnlyList<Message> updated)
  {
    ArgumentNullException.ThrowIfNull(updated);
    if (updated.Count == 0)
    {
      return Result.Failure<bool>(new DomainError("EmptyConversation",
          "Compaction cannot empty the conversation."));
    }

    if (ValidateReplacement(updated) is { } error)
    {
      return Result.Failure<bool>(error);
    }

    _messages.Clear();
    _messages.AddRange(updated);
    NotifyReplaced();
    return Result.Success(true);
  }

  /// <summary>Checks the tool-call/result pairing protocol over a candidate replacement:
  ///     every tool result answers an earlier assistant tool call, and every assistant
  ///     tool call receives a later tool result — save the single trailing unanswered
  ///     call, which is the call in flight while a tool mutates the conversation. Null
  ///     when the replacement is valid, otherwise the first violated invariant.</summary>
  private static DomainError? ValidateReplacement(IReadOnlyList<Message> updated)
  {
    HashSet<string> inFlight = InFlightCallIds(updated);
    for (int i = 0; i < updated.Count; i++)
    {
      if (ValidateToolResult(updated, i) is { } resultError)
      {
        return resultError;
      }

      if (ValidateToolCalls(updated, i, inFlight) is { } callsError)
      {
        return callsError;
      }
    }

    return null;
  }

  /// <summary>The calls considered in flight: every tool call of the LAST message when
  ///     that message is an assistant message carrying calls. The loop appends the
  ///     assistant message before dispatching, so while a tool runs its own call — and
  ///     every sibling of a parallel batch — is trailing with no result yet. Empty when
  ///     the trailing message carries no calls, granting no leniency at all.</summary>
  private static HashSet<string> InFlightCallIds(IReadOnlyList<Message> updated)
  {
    Message last = updated[^1];
    return last.Role is Role.Assistant && last.ToolCalls is { Count: > 0 } calls
        ? calls.Select(call => call.Id).ToHashSet(StringComparer.Ordinal)
        : new HashSet<string>(StringComparer.Ordinal);
  }

  /// <summary>Checks the tool result at <paramref name="index"/>: it must carry a tool
  ///     call id answered by an earlier assistant message. Null when valid.</summary>
  private static DomainError? ValidateToolResult(IReadOnlyList<Message> updated, int index)
  {
    Message message = updated[index];
    if (message.Role is not Role.Tool)
    {
      return null;
    }

    if (string.IsNullOrEmpty(message.ToolCallId))
    {
      return new DomainError("DanglingToolResult",
          $"Tool result at position {index} has no earlier matching assistant tool call.");
    }

    bool answered = updated.Take(index).Any(m =>
        m.Role is Role.Assistant && m.ToolCalls?.Any(c => c.Id == message.ToolCallId) == true);
    return answered
        ? null
        : new DomainError("DanglingToolResult",
            $"Tool result at position {index} has no earlier matching assistant tool call: {message.ToolCallId}.");
  }

  /// <summary>Checks every tool call of the assistant message at <paramref name="index"/>:
  ///     each must receive a later tool result, except the calls in
  ///     <paramref name="inFlight"/> (the trailing batch still executing). Null when all
  ///     are answered.</summary>
  private static DomainError? ValidateToolCalls(IReadOnlyList<Message> updated, int index, HashSet<string> inFlight)
  {
    Message message = updated[index];
    if (message.Role is not Role.Assistant || message.ToolCalls is not { Count: > 0 } calls)
    {
      return null;
    }

    ToolCall? unanswered = calls.FirstOrDefault(call =>
        !inFlight.Contains(call.Id)
        && !updated.Skip(index + 1).Any(m => m.Role is Role.Tool && m.ToolCallId == call.Id));
    return unanswered is null
        ? null
        : new DomainError("UnansweredToolCall",
            $"Assistant tool call {unanswered.Id} at position {index} has no later tool result.");
  }
}
