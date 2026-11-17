using System.Text;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

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

  /// <summary>THE PRICE OF THE REWRITE (issue #267), rendered onto every tool-result
  ///     shrink's sentinel. A provider's prompt cache is a PREFIX cache, so rewriting
  ///     bytes in the middle of the array re-bills everything from the first changed
  ///     message onward; this states that trade as numbers beside the savings the trim
  ///     bought. The trim is never gated on it - the report exists so the trade is
  ///     visible instead of invisible.</summary>
  private static string Cost(IReadOnlyList<Message> messages, int firstChangedIndex, long savedChars) =>
      ContextShrinkCost.Of(messages, firstChangedIndex, savedChars).Render();

  /// <summary>Renders the current message list: one '[i] [Role] content' line per
  ///     message, embedded newlines as indented continuation lines, tool calls as
  ///     'tool_call(id): name(arguments)'.</summary>
  public string List()
  {
    List<string> lines = [];
    IReadOnlyList<Message> messages = _conversation.Messages;
    ImageResendPlan plan = ImageResendPlan.For(messages);
    for (int i = 0; i < messages.Count; i++)
    {
      AppendRendered(lines, i + 1, messages[i], plan);
    }

    return lines.Count == 0 ? "[context: empty]" : string.Join("\n", lines);
  }

  /// <summary>Renders the TRIMMABLE tool results, one line each, biggest first: the
  ///     ordinal a trim names ('rN'), the message position Remove and Shorten name,
  ///     the tool that produced it, its size, and the arguments that identify it.
  ///     Only results that still hold their content are listed - a result already
  ///     trimmed is not a trim candidate. Capped at <see cref="ListLimit"/> lines;
  ///     the count left out is stated so the list is never mistaken for the whole.</summary>
  public string ListTrimmable()
  {
    IReadOnlyList<Message> messages = _conversation.Messages;
    List<(int Ordinal, int Position, Message Message, ToolCall? Call)> rows = [];
    int ordinal = 0;
    for (int i = 0; i < messages.Count; i++)
    {
      if (messages[i].Role is not Role.Tool)
      {
        continue;
      }

      ordinal++;
      if (IsTrimmed(messages[i]))
      {
        continue;
      }

      rows.Add((ordinal, i + 1, messages[i], PairedCall(messages, i)));
    }

    if (rows.Count == 0)
    {
      return "[context: no trimmable tool results]";
    }

    List<(int Ordinal, int Position, Message Message, ToolCall? Call)> ordered =
        [.. rows.OrderByDescending(r => r.Message.Content.Length).ThenBy(r => r.Ordinal)];
    List<string> lines = [];
    foreach ((int Ordinal, int Position, Message Message, ToolCall? Call) row in ordered.Take(ListLimit))
    {
      int tokens = row.Message.Content.Length / ToolResultMarker.CharsPerToken;
      lines.Add($"r{row.Ordinal}\tmsg {row.Position}\t{Describe(row.Call)}\t~{tokens} tok\t{Identify(row.Call)}{Parts(row.Message)}");
    }

    int omitted = ordered.Count - lines.Count;
    string header = omitted > 0
        ? $"[context: {ordered.Count} trimmable result(s); {omitted} smaller one(s) omitted - trim these, or name ordinals directly]"
        : $"[context: {ordered.Count} trimmable result(s)]";
    return header + "\n" + string.Join("\n", lines);
  }

  /// <summary>How many rows the listing renders before it starts omitting.</summary>
  public const int ListLimit = 40;

  private const int IdentifyLimit = 60;

  /// <summary>The tool name for a row: the paired call's name, or a generic marker when
  ///     the pairing is unavailable.</summary>
  private static string Describe(ToolCall? call) => call?.Name ?? "tool";

  /// <summary>The identifying arguments for a row: the paired call's arguments on one
  ///     line, truncated. Empty when the pairing is unavailable.</summary>
  private static string Identify(ToolCall? call)
  {
    if (call is null)
    {
      return "";
    }

    string flat = call.Arguments.Replace('\r', ' ').Replace('\n', ' ').Trim();
    return flat.Length <= IdentifyLimit ? flat : flat[..IdentifyLimit] + "...";
  }

  /// <summary>A listing must never hide a picture: a result carrying image parts says so,
  ///     or a model choosing what to trim would discard an image it never saw named.</summary>
  private static string Parts(Message message) =>
      message.Parts is { Count: > 0 } parts && parts.Any(p => p is MessagePart.ImagePart)
          ? "  [image]"
          : "";

  /// <summary>True when the result already holds a relocation marker rather than its
  ///     original content.</summary>
  private static bool IsTrimmed(Message message) =>
      message.Content.StartsWith(ToolResultMarker.Prefix, StringComparison.Ordinal);

  /// <summary>The tool call a result answers, searched backwards through the list.</summary>
  private static ToolCall? PairedCall(IReadOnlyList<Message> messages, int toolResultIndex)
  {
    string? id = messages[toolResultIndex].ToolCallId;
    if (id is null)
    {
      return null;
    }

    for (int i = toolResultIndex - 1; i >= 0; i--)
    {
      if (messages[i].ToolCalls?.FirstOrDefault(c => c.Id == id) is { } call)
      {
        return call;
      }
    }

    return null;
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
    List<Message> updated = [with(messages.Count - doomed.Count)];
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

  /// <summary>Replaces the selected tool results' content in place with relocation
  ///     markers. The selection names tool-result ordinals ('rN'), 'last N', or
  ///     nothing for every trimmable result outside the recent tail. A selection that
  ///     names something untrimmable trims the rest and reports what it skipped - the
  ///     model's intent is the trim, not the validation. Pairing ids stay, so the
  ///     aggregate's tool-call/result invariants hold by construction.</summary>
  public Result<string> TrimToolResults(string selection)
  {
    Result<TrimSelection> parsed = TrimSelection.Parse(selection);
    if (!parsed.IsSuccess)
    {
      return Result.Failure<string>(parsed.Error);
    }

    IReadOnlyList<Message> messages = _conversation.Messages;
    List<int> toolPositions = [];
    for (int i = 0; i < messages.Count; i++)
    {
      if (messages[i].Role is Role.Tool)
      {
        toolPositions.Add(i);
      }
    }

    List<string> skipped = [];
    List<int> trimmable = ResolveTrimPositions(parsed.Value, messages, toolPositions, skipped);
    if (trimmable.Count == 0)
    {
      return Result.Failure<string>(new DomainError("NothingToTrim",
          NothingToTrimReason(parsed.Value, skipped)));
    }

    List<Message> updated = [.. messages];
    long savedChars = 0;
    foreach (int position in trimmable)
    {
      string marker = ToolResultMarker.For(messages, position);
      savedChars += messages[position].Content.Length - marker.Length;
      updated[position] = updated[position] with { Content = marker };
    }

    string trimmedNote = string.Join(",", trimmable.Select(p => $"r{toolPositions.IndexOf(p) + 1}"));
    string skippedNote = skipped.Count == 0 ? "" : $" skipped: {string.Join(", ", skipped)}.";
    Result<bool> applied = _conversation.Compact(updated);
    return applied.IsSuccess
        ? Result.Success($"{ShrankSentinel} {trimmable.Count} message(s): trimmed {trimmedNote}.{skippedNote}] {Cost(messages, trimmable.Min(), savedChars)}")
        : Result.Failure<string>(applied.Error);
  }

  /// <summary>Why a trim selected nothing: the skipped items when the selection named some,
  ///     the default window when it named none, and a plain statement otherwise.</summary>
  private static string NothingToTrimReason(TrimSelection selection, List<string> skipped)
  {
    return skipped.Count > 0
        ? $"Nothing selected can be trimmed: {string.Join(", ", skipped)}."
        : DefaultNothingToTrimReason(selection);
  }

  private static string DefaultNothingToTrimReason(TrimSelection selection)
  {
    return selection.Ordinals is null && selection.LastCount is null
        ? $"No trimmable result outside the most recent {TrimSelection.DefaultProtectedTail}; "
            + "name ordinals with 'selection' to trim inside that window."
        : "Nothing selected to trim.";
  }

  /// <summary>Maps a parsed selection onto the positions it names, keeping only the
  ///     results that still hold content. Anything named but untrimmable is recorded
  ///     in <paramref name="skipped"/> and the rest proceeds.</summary>
  private static List<int> ResolveTrimPositions(TrimSelection selection, IReadOnlyList<Message> messages,
      List<int> toolPositions, List<string> skipped)
  {
    List<int> chosen = [];
    if (selection.Ordinals is { } ordinals)
    {
      foreach (int ordinal in ordinals)
      {
        if (ordinal > toolPositions.Count)
        {
          skipped.Add($"r{ordinal} (no such tool result; the list has {toolPositions.Count})");
        }
        else
        {
          chosen.Add(toolPositions[ordinal - 1]);
        }
      }
    }
    else if (selection.LastCount is { } last)
    {
      chosen.AddRange(toolPositions.Skip(Math.Max(0, toolPositions.Count - last)));
    }
    else
    {
      chosen.AddRange(toolPositions.Take(Math.Max(0, toolPositions.Count - TrimSelection.DefaultProtectedTail)));
    }

    List<int> trimmable = [];
    foreach (int position in chosen.Distinct().Order())
    {
      if (IsTrimmed(messages[position]))
      {
        skipped.Add($"r{toolPositions.IndexOf(position) + 1} (already trimmed)");
      }
      else
      {
        trimmable.Add(position);
      }
    }

    return trimmable;
  }

  /// <summary>Replaces ONE selected tool result's content with the caller's excerpt,
  ///     keeping its role, tool-call id, and timestamp exactly. Same tool-only guard
  ///     as <see cref="TrimToolResults"/>.</summary>
  public Result<string> ShortenToolResult(string selection, string text)
  {
    Result<ContextSelection> parsed = ContextSelection.Parse(selection);
    if (!parsed.IsSuccess)
    {
      return Result.Failure<string>(parsed.Error);
    }

    if (string.IsNullOrWhiteSpace(text))
    {
      return Result.Failure<string>(new DomainError("InvalidText", "Shorten needs non-empty replacement text."));
    }

    Result<IReadOnlyList<int>> resolved = ResolveToolOnlySelection(parsed.Value, requireSingle: true);
    if (!resolved.IsSuccess)
    {
      return Result.Failure<string>(resolved.Error);
    }

    int position = resolved.Value[0];
    List<Message> updated = [.. _conversation.Messages];
    long savedChars = updated[position].Content.Length - text.Length;
    updated[position] = updated[position] with { Content = text };
    Result<bool> applied = _conversation.Compact(updated);
    return applied.IsSuccess
        ? Result.Success($"{ShrankSentinel} 1 message(s): message {position + 1} shortened.] {Cost(updated, position, savedChars)}")
        : Result.Failure<string>(applied.Error);
  }

  /// <summary>Resolves a selection for a tool-result edit: every named position must
  ///     be a Tool message — user, assistant, and system messages are out of reach
  ///     by design; the first violation fails and names its 1-based position.</summary>
  private Result<IReadOnlyList<int>> ResolveToolOnlySelection(ContextSelection selection, bool requireSingle)
  {
    IReadOnlyList<Message> messages = _conversation.Messages;
    Result<IReadOnlyList<int>> resolved = selection.Resolve(messages.Count);
    if (!resolved.IsSuccess)
    {
      return resolved;
    }

    IReadOnlyList<int> positions = resolved.Value;
    if (positions.Count == 0)
    {
      return Result.Failure<IReadOnlyList<int>>(new DomainError("NothingSelected",
          $"Selection '{selection.Render()}' selects no messages."));
    }

    if (requireSingle && positions.Count != 1)
    {
      return Result.Failure<IReadOnlyList<int>>(new DomainError("AmbiguousSelection",
          $"Shorten needs exactly one message; '{selection.Render()}' selects {positions.Count}."));
    }

    foreach (int position in positions)
    {
      if (messages[position].Role != Role.Tool)
      {
        return Result.Failure<IReadOnlyList<int>>(new DomainError("ToolOnlySelection",
            $"Selection '{selection.Render()}' names message {position + 1}, which is not a tool result; only tool results can be trimmed."));
      }
    }

    return Result.Success(positions);
  }

  /// <summary>One message's line: its content, its tool calls, its parts render, and the tool
  ///     call it answers. <paramref name="plan"/> is the resend decision the parts render consults,
  ///     so an image the request will not re-send reads here as the marker rather than as a
  ///     picture - the listing must describe the request the model will actually see.</summary>
  private static void AppendRendered(List<string> lines, int number, Message message,
      ImageResendPlan plan)
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
      lines[^1] += (lines[^1].EndsWith(' ', StringComparison.Ordinal) ? "" : " ") + rendered;
    }

    if (message.Parts is { Count: > 0 })
    {
      StringBuilder sb = new();
      ImagePartMarker.AppendParts(sb, PartRenders.Of(message.Parts, plan, number - 1));
      lines[^1] += (lines[^1].EndsWith(' ', StringComparison.Ordinal) ? "" : " ") + sb.ToString().Trim();
    }

    if (message.ToolCallId is not null)
    {
      lines[^1] += (lines[^1].EndsWith(' ', StringComparison.Ordinal) ? "" : " ") + $"[answers {message.ToolCallId}]";
    }
  }
}
