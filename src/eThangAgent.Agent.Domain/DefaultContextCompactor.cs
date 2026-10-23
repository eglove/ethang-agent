using System.Text;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

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

  /// <summary>Fraction of the character budget above which a kept tool result is a
  ///     tail-compression candidate (deterministic marker, no LLM call).</summary>
  public const double TailCompressionThresholdFraction = 0.05;

  /// <summary>True when the last CompactAsync decided the next compaction should be a
  ///     SESSION HANDOFF (issue #234): the turn ends, a new session is launched with the
  ///     summary as its seed, and the old session completes gracefully. The consumer
  ///     (Agent.cs) checks this after a successful CompactAsync and acts on it. A prefix
  ///     eviction sets it; the tail-compression fallback never does (it produces no
  ///     summary, and a handoff needs one). Each CompactAsync call re-decides: the flag
  ///     resets at the start of every call.</summary>
  public bool HandoffRequested { get; private set; }

  public async Task<Result<CompactionOutcome>> CompactAsync(Conversation conversation,
      ModelConfig servingModel, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(conversation);
    ArgumentNullException.ThrowIfNull(servingModel);
    IReadOnlyList<Message> messages = conversation.Messages; // snapshot, not live view
    // Each call re-decides the handoff: the flag is one-shot per compaction.
    HandoffRequested = false;
    // A serving model whose window is not yet catalog-resolved cannot plan an
    // eviction: fail with a named code instead of guessing a window.
    if (servingModel.ContextWindow is not { } window)
    {
      return Result.Failure<CompactionOutcome>(new DomainError("UnknownContextWindow",
          "Serving model has no resolved context window; compaction cannot plan."));
    }

    CompactionPolicy policy = CompactionPolicy.For(window);
    ContextEvictionPlan plan = ContextEvictionPolicy.Plan(messages, window, policy.TargetPercent);
    if (plan.EvictCount == 0)
    {
      return CompressTailFallback(conversation, messages, window, policy.TargetPercent);
    }

    List<Message> evicted = [.. messages.Skip(plan.ProtectedCount).Take(plan.EvictCount)];
    List<Message> keptTail = CompressTail(messages, plan, window, policy.TargetPercent);
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

    // A prefix eviction produced a real summary: the handoff decision rides THIS
    // compaction (issue #234) - the consumer reads it right after success.
    HandoffRequested = true;
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

  /// <summary>The eviction-failed fallback: the kept tail's 3-group floor alone
  ///     exceeds the target (tail-dominated bloat - huge recent tool results), so
  ///     compression IS the lever. When at least one oversized result compresses,
  ///     the deep clean succeeds WITHOUT a summarizer call and WITHOUT a summary
  ///     message: nothing was evicted, so the replacement is the compressed kept
  ///     list via <see cref="Conversation.Compact"/>. With no compressible
  ///     candidates at all, the deep clean fails exactly as before.</summary>
  private static Result<CompactionOutcome> CompressTailFallback(Conversation conversation,
      IReadOnlyList<Message> messages, int window, double targetPercent)
  {
    ContextEvictionPlan plan = new(ProtectedCount: 0, EvictCount: 0, KeptCount: messages.Count);
    List<Message> keptTail = CompressTail(messages, plan, window, targetPercent);
    if (keptTail.Select(x => x.Content).SequenceEqual(messages.Select(x => x.Content)))
    {
      return Result.Failure<CompactionOutcome>(new DomainError("CompactionImpossible",
          "Nothing outside the kept tail to evict."));
    }

    Result<bool> compacted = conversation.Compact(keptTail);
    return compacted.IsSuccess
        ? Result.Success(new CompactionOutcome(0, keptTail.Count, null))
        : Result.Failure<CompactionOutcome>(compacted.Error);
  }

  /// <summary>Compresses the kept tail's oversized tool results to deterministic
  ///     relocation markers, oldest-first, until the tail's estimated size fits the
  ///     target budget. Only the content is replaced — role, tool-call id, timestamp,
  ///     and parts stay intact, so <see cref="Conversation.Compact"/>'s pairing
  ///     invariants hold by construction. Kept carries absolute indexes so the marker
  ///     can find the paired tool call in the ORIGINAL list.</summary>
  private static List<Message> CompressTail(IReadOnlyList<Message> messages, ContextEvictionPlan plan,
      int window, double targetPercent)
  {
    long budgetChars = (long)window * ContextEvictionPolicy.CharsPerTokenEstimate;
    long targetChars = (long)(budgetChars * targetPercent / 100);
    long thresholdChars = (long)(budgetChars * TailCompressionThresholdFraction);

    List<(int Index, Message Message)> kept = [.. messages.Select((m, i) => (i, m))
        .Where(x => x.i < plan.ProtectedCount || x.i >= plan.ProtectedCount + plan.EvictCount)];
    long keptSize = ContextEvictionPolicy.SizeOf(kept.Select(x => x.Message));
    for (int k = 0; k < kept.Count && keptSize > targetChars; k++)
    {
      if (kept[k].Message.Role is not Role.Tool || kept[k].Message.Content.Length <= thresholdChars)
      {
        continue;
      }

      string marker = ToolResultMarker.For(messages, kept[k].Index);
      keptSize -= kept[k].Message.Content.Length - marker.Length;
      kept[k] = (kept[k].Index, kept[k].Message with { Content = marker });
    }

    return [.. kept.Select(x => x.Message)];
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

  /// <summary>Appends the message's parts render to <paramref name="sb"/> - the
  ///     shared renderer (ImagePartMarker, Tool Domain): an image part becomes the
  ///     text marker '[image: mediaType, N bytes]' (N = decoded byte length), a text
  ///     part appends verbatim. Pinned by tests.</summary>
  private static void AppendParts(StringBuilder sb, Message message)
  {
    List<PartRender> parts = [.. message.Parts!.Select(part => part is MessagePart.ImagePart image
        ? PartRender.ForImage(image.MediaType, image.Base64Data)
        : PartRender.ForText(((MessagePart.TextPart)part).Text))];
    ImagePartMarker.AppendParts(sb, parts);
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

    if (message.Parts is { Count: > 0 })
    {
      AppendParts(sb, message);
    }

    if (message.ToolCallId is not null)
    {
      _ = sb.Append(" [answers ").Append(message.ToolCallId).Append(']');
    }

    _ = sb.AppendLine();
  }
}
