using eThangAgent.SharedKernel;

namespace eThangAgent.ConversationDomain;

public class Conversation(IEnumerable<Message>? seed = null)
{
  private readonly List<Message> _messages = [.. seed ?? []];

  public IReadOnlyList<Message> Messages => _messages.AsReadOnly();

  public void AddUserMessage(string text)
      => _messages.Add(new Message(Role.User, text, DateTimeOffset.UtcNow));

  public void AddAssistantMessage(string text)
      => _messages.Add(new Message(Role.Assistant, text, DateTimeOffset.UtcNow));

  public void AddAssistantMessage(string text, IReadOnlyList<ToolCall>? toolCalls)
      => _messages.Add(new Message(Role.Assistant, text, DateTimeOffset.UtcNow, toolCalls));

  public void AddToolResult(string toolCallId, string content)
      => _messages.Add(new Message(Role.Tool, content, DateTimeOffset.UtcNow, ToolCallId: toolCallId));

  /// <summary>Appends a system-level message (e.g. a turn-boundary nudge). Null/whitespace is rejected.</summary>
  public void AddSystemMessage(string text)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(text);
    _messages.Add(new Message(Role.System, text, DateTimeOffset.UtcNow));
  }

  /// <summary>Replaces the whole message list (compaction). The aggregate enforces its
  ///     protocol invariants before mutating: the replacement is non-empty, every tool
  ///     result answers an earlier assistant tool call, and every assistant tool call
  ///     receives a later result. Violations leave the conversation untouched.</summary>
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
    return Result.Success(true);
  }

  /// <summary>Checks the tool-call/result pairing protocol over a candidate replacement:
  ///     every tool result answers an earlier assistant tool call, and every assistant
  ///     tool call receives a later tool result. Null when the replacement is valid,
  ///     otherwise the first violated invariant.</summary>
  private static DomainError? ValidateReplacement(IReadOnlyList<Message> updated)
  {
    for (int i = 0; i < updated.Count; i++)
    {
      if (ValidateToolResult(updated, i) is { } resultError)
      {
        return resultError;
      }

      if (ValidateToolCalls(updated, i) is { } callsError)
      {
        return callsError;
      }
    }

    return null;
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
  ///     each must receive a later tool result. Null when all are answered.</summary>
  private static DomainError? ValidateToolCalls(IReadOnlyList<Message> updated, int index)
  {
    Message message = updated[index];
    if (message.Role is not Role.Assistant || message.ToolCalls is not { Count: > 0 } calls)
    {
      return null;
    }

    ToolCall? unanswered = calls.FirstOrDefault(call =>
        !updated.Skip(index + 1).Any(m => m.Role is Role.Tool && m.ToolCallId == call.Id));
    return unanswered is null
        ? null
        : new DomainError("UnansweredToolCall",
            $"Assistant tool call {unanswered.Id} at position {index} has no later tool result.");
  }
}
