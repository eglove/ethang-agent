using System.Text;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Evicts the oldest conversation prefix per <see cref="ContextEvictionPolicy"/>,
///     has a summarizer model write a dense handoff summary of it, and replaces the
///     prefix with that summary via <see cref="Conversation.Compact"/>. The summarizer
///     model is resolved per compaction through the factory-supplied delegate; when it
///     returns null the serving model writes its own summary (fallback, by design).
///     Exact render strings are pinned by tests.</summary>
public sealed class DefaultContextCompactor(IModelProviderFactory providerFactory, Func<ModelConfig?> summarizerModel)
    : IContextCompactor
{
  /// <summary>Verbatim contract for the summarizer call.</summary>
  public const string SummarizerSystemPrompt =
      "You are compacting an agent conversation. Produce a dense handoff summary of the evicted messages that lets work continue seamlessly: decisions made, files touched, current state, next steps. Keep concrete identifiers. Do not narrate.";

  public async Task<Result<CompactionOutcome>> CompactAsync(Conversation conversation,
      ModelConfig servingModel, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(conversation);
    ArgumentNullException.ThrowIfNull(servingModel);
    IReadOnlyList<Message> messages = conversation.Messages; // snapshot, not live view
    ContextEvictionPlan plan = ContextEvictionPolicy.Plan(messages, servingModel.ContextWindow);
    if (plan.EvictCount == 0)
    {
      return Result.Failure<CompactionOutcome>(new DomainError("CompactionImpossible",
          "Nothing outside the kept tail to evict."));
    }

    List<Message> evicted = [.. messages.Take(plan.EvictCount)];
    List<Message> keptTail = [.. messages.Skip(plan.EvictCount)];
    ModelConfig writer = summarizerModel() ?? servingModel;
    IModelProvider provider = providerFactory.Create(writer);
    ModelRequest request = new(
        [new Message(Role.User, Render(evicted, keptTail), DateTimeOffset.UtcNow)],
        SystemPrompt: SummarizerSystemPrompt);
    Result<ModelResponse> summary = await provider.SendAsync(writer, request, ct).ConfigureAwait(false);
    if (!summary.IsSuccess)
    {
      return Result.Failure<CompactionOutcome>(summary.Error);
    }

    string summaryText = summary.Value.Content?.Trim() ?? "";
    if (summaryText.Length == 0)
    {
      return Result.Failure<CompactionOutcome>(new DomainError("EmptySummary",
          "The summarizer model returned no summary text."));
    }

    List<Message> replacement =
    [
        new(Role.System, $"[Conversation summary — earlier messages were compacted.]\n{summaryText}",
            DateTimeOffset.UtcNow, IsSummary: true),
        ..keptTail,
    ];
    Result<bool> compacted = conversation.Compact(replacement);
    return compacted.IsSuccess
        ? Result.Success(new CompactionOutcome(plan.EvictCount, keptTail.Count, summary.Value.Usage))
        : Result.Failure<CompactionOutcome>(compacted.Error);
  }

  /// <summary>Exact wire render of the compaction request: verbatim Role: content lines,
  ///     tool calls as tool_call(id): name(arguments). Pinned by tests.</summary>
  public static string Render(IReadOnlyList<Message> evicted, IReadOnlyList<Message> keptTail)
  {
    ArgumentNullException.ThrowIfNull(evicted);
    ArgumentNullException.ThrowIfNull(keptTail);
    StringBuilder sb = new();
    _ = sb.AppendLine("## Evicted messages (to summarize)");
    foreach (Message message in evicted)
    {
      AppendMessage(sb, message);
    }

    _ = sb.AppendLine("## Kept tail (recent context, for continuity)");
    foreach (Message message in keptTail)
    {
      AppendMessage(sb, message);
    }

    return sb.ToString();
  }

  private static void AppendMessage(StringBuilder sb, Message message)
  {
    _ = sb.Append('[').Append(message.Role).Append("] ").Append(message.Content);
    if (message.ToolCalls is { Count: > 0 } calls)
    {
      foreach (ToolCall call in calls)
      {
        _ = sb.Append(" tool_call(").Append(call.Id).Append("): ").Append(call.Name).Append('(').Append(call.Arguments).Append(')');
      }
    }

    if (message.ToolCallId is not null)
    {
      _ = sb.Append(" [answers ").Append(message.ToolCallId).Append(']');
    }

    _ = sb.AppendLine();
  }
}
