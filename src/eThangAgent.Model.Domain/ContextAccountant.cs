namespace eThangAgent.ModelDomain;

/// <summary>Default <see cref="IContextMonitor"/>: keeps the provider-scored context size,
///     accumulated session totals, and the utilization decision input. Thread-safe: reports
///     arrive from provider calls on arbitrary threads. The context size is the LAST
///     request's input tokens - what the provider actually scored, covering system prompt,
///     tools, and full history. Character-based numbers appear only in the breakdown
///     estimate and never feed compaction decisions.
/// <para>Prompt-cache and reasoning counts (issue #262) accumulate beside the token
///     totals. A report that omits either count leaves the LAST value NULL rather than
///     zero - the provider unreported it, and a fabricated zero would claim the cache
///     was measured cold or that reasoning billed nothing. The session totals keep
///     adding only what was actually reported.</para></summary>
public sealed class ContextAccountant(int? contextWindow) : IContextMonitor
{
  private readonly Lock _gate = new();
  private readonly int? _contextWindow = contextWindow;
  private int? _lastInputTokens;
  private long _totalInputTokens;
  private long _totalOutputTokens;
  private int? _lastCachedInputTokens;
  private long _totalCachedInputTokens;
  private int? _lastReasoningTokens;
  private long _totalReasoningTokens;
  private ContextComposition? _lastComposition;

  public void OnRequestUsage(TokenUsage usage, ContextComposition composition)
  {
    lock (_gate)
    {
      _lastInputTokens = usage.InputTokens;
      _totalInputTokens += usage.InputTokens;
      _totalOutputTokens += usage.OutputTokens;
      _lastCachedInputTokens = usage.CachedInputTokens;
      _totalCachedInputTokens += usage.CachedInputTokens ?? 0;
      _lastReasoningTokens = usage.ReasoningTokens;
      _totalReasoningTokens += usage.ReasoningTokens ?? 0;
      _lastComposition = composition;
    }
  }

  public ContextStatus Status
  {
    get
    {
      lock (_gate)
      {
        return new ContextStatus(
            _lastInputTokens,
            _totalInputTokens,
            _totalOutputTokens,
            _contextWindow,
            _lastInputTokens is { } last && _contextWindow is { } window
                ? Math.Round(last * 100.0 / window, 2)
                : null,
            _lastCachedInputTokens,
            _totalCachedInputTokens,
            _lastReasoningTokens,
            _totalReasoningTokens);
      }
    }
  }

  public ContextBreakdown? Breakdown
  {
    get
    {
      lock (_gate)
      {
        return _lastInputTokens is not { } last || _lastComposition is null
            ? null
            : Estimate(last, _lastComposition);
      }
    }
  }

  private static ContextBreakdown Estimate(int last, ContextComposition composition)
  {
    long total = composition.SystemPromptChars + composition.MessageChars + composition.ToolDefinitionChars;
    return total == 0
        ? new ContextBreakdown(null, null, null)
        : new ContextBreakdown(
            (int)Math.Round(last * (double)composition.SystemPromptChars / total),
            (int)Math.Round(last * (double)composition.MessageChars / total),
            (int)Math.Round(last * (double)composition.ToolDefinitionChars / total));
  }
}
