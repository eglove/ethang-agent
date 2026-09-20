using eThangAgent.SharedKernel;

namespace eThangAgent.ConversationDomain;

/// <summary>Fine-grained context surgery over one live conversation: render the
///     message list with stable 1-based indexes, remove or shorten selected
///     messages, and let the agent compact its own context at a milestone.
///     Every mutation rides <see cref="Conversation.Compact"/>, so the aggregate's
///     tool-call/result protocol invariants hold by construction; on any violation
///     the conversation is left untouched and the failure surfaces typed. A shrink
///     returns the verbatim sentinel <c>[context: shrank N message(s)]</c> — the
///     contract loop persistence reads to replace (not append) the transcript.</summary>
public sealed class ConversationContextService(Conversation conversation)
{
  /// <summary>Sentinel prefix of every shrink result. Persisters read this to switch
  ///     from append-slice to whole-transcript replacement.</summary>
  public const string ShrankSentinel = "[context: shrank";

  private readonly Conversation _conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));

  /// <summary>Renders the current message list: one '[i] [Role] content' line per
  ///     message, embedded newlines as indented continuation lines, tool calls as
  ///     'tool_call(id): name(arguments)'.</summary>
  public string List()
  {
    List<string> lines = [];
    IReadOnlyList<Message> messages = _conversation.Messages;
    for (int i = 0; i < messages.Count; i++)
    {
      AppendRendered(lines, i + 1, messages[i]);
    }

    return lines.Count == 0 ? "[context: empty]" : string.Join("\n", lines);
  }

  /// <summary>Removes the selected messages. The selection rides a strict parser
  ///     ('last N' or 'S..E'); the replacement must stay non-empty, keep every
  ///     tool-call/result pair together, and keep a System-or-User head.</summary>
  public Result<string> Remove(string selection)
  {
    Result<ContextSelection> parsed = ContextSelection.Parse(selection);
    return parsed.IsSuccess
        ? Shrink(parsed.Value)
        : Result.Failure<string>(parsed.Error);
  }

  /// <summary>Replaces one selected message's content, keeping its role, tool calls,
  ///     tool-call id, and timestamp exactly. The selection must name exactly one
  ///     message; the replacement text must be non-empty.</summary>
  public Result<string> Shorten(string selection, string text)
  {
    Result<ContextSelection> parsed = ContextSelection.Parse(selection);
    if (!parsed.IsSuccess)
    {
      return Result.Failure<string>(parsed.Error);
    }

    Result<IReadOnlyList<int>> resolved = parsed.Value.Resolve(_conversation.Messages.Count);
    if (!resolved.IsSuccess)
    {
      return Result.Failure<string>(resolved.Error);
    }

    IReadOnlyList<int> positions = resolved.Value;
    if (positions.Count != 1)
    {
      return Result.Failure<string>(new DomainError("AmbiguousSelection",
          $"Shorten needs exactly one message; '{selection}' selects {positions.Count}."));
    }

    if (string.IsNullOrWhiteSpace(text))
    {
      return Result.Failure<string>(new DomainError("InvalidText", "Shorten needs non-empty replacement text."));
    }

    int index = positions[0];
    List<Message> updated = [.. _conversation.Messages];
    updated[index] = updated[index] with { Content = text };
    Result<bool> applied = _conversation.Compact(updated);
    return applied.IsSuccess
        ? Result.Success($"{ShrankSentinel} 1 message(s): message {index + 1} shortened.]")
        : Result.Failure<string>(applied.Error);
  }

  /// <summary>Applies a removal: resolves the selection, builds the replacement list,
  ///     enforces the head-protocol rule, and commits via the aggregate.</summary>
  private Result<string> Shrink(ContextSelection selection)
  {
    IReadOnlyList<Message> messages = _conversation.Messages;
    Result<IReadOnlyList<int>> resolved = selection.Resolve(messages.Count);
    if (!resolved.IsSuccess)
    {
      return Result.Failure<string>(resolved.Error);
    }

    IReadOnlyList<int> positions = resolved.Value;
    if (positions.Count == 0)
    {
      return Result.Failure<string>(new DomainError("NothingSelected",
          $"Selection '{selection.Render()}' selects no messages."));
    }

    HashSet<int> doomed = [.. positions];
    List<Message> updated = new(messages.Count - doomed.Count);
    for (int i = 0; i < messages.Count; i++)
    {
      if (!doomed.Contains(i))
      {
        updated.Add(messages[i]);
      }
    }

    if (updated.Count > 0 && updated[0].Role is not (Role.System or Role.User))
    {
      return Result.Failure<string>(new DomainError("OrphanHead",
          "The first message must be a user or system message; removing the selection would orphan the head."));
    }

    Result<bool> applied = _conversation.Compact(updated);
    return applied.IsSuccess
        ? Result.Success($"{ShrankSentinel} {doomed.Count} message(s): removed {selection.Render()}.]")
        : Result.Failure<string>(applied.Error);
  }

  private static void AppendRendered(List<string> lines, int number, Message message)
  {
    string[] content = (message.Content ?? "").Split('\n');
    lines.Add($"[{number}] [{message.Role}] " + content[0]);
    for (int i = 1; i < content.Length; i++)
    {
      lines.Add("  " + content[i]);
    }

    if (message.ToolCalls is { Count: > 0 } calls)
    {
      string rendered = string.Join(" ", calls.Select(call => $"tool_call({call.Id}): {call.Name}({call.Arguments})"));
      lines[^1] += (lines[^1].EndsWith(' ') ? "" : " ") + rendered;
    }

    if (message.ToolCallId is not null)
    {
      lines[^1] += (lines[^1].EndsWith(' ') ? "" : " ") + $"[answers {message.ToolCallId}]";
    }
  }
}
