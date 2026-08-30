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

    for (int i = 0; i < updated.Count; i++)
    {
      Message message = updated[i];
      if (message.Role is Role.Tool && (string.IsNullOrEmpty(message.ToolCallId) ||
          !updated.Take(i).Any(m => m.Role is Role.Assistant && m.ToolCalls?.Any(c => c.Id == message.ToolCallId) == true)))
      {
        return Result.Failure<bool>(new DomainError("DanglingToolResult",
            $"Tool result at position {i} has no earlier matching assistant tool call" +
            (message.ToolCallId is null ? "." : $": {message.ToolCallId}.")));
      }

      if (message.Role is Role.Assistant && message.ToolCalls is { Count: > 0 } calls)
      {
        ToolCall? unanswered = calls.FirstOrDefault(call =>
            !updated.Skip(i + 1).Any(m => m.Role is Role.Tool && m.ToolCallId == call.Id));
        if (unanswered is not null)
        {
          return Result.Failure<bool>(new DomainError("UnansweredToolCall",
              $"Assistant tool call {unanswered.Id} at position {i} has no later tool result."));
        }
      }
    }

    _messages.Clear();
    _messages.AddRange(updated);
    return Result.Success(true);
  }
}
