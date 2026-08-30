using eThangAgent.ModelDomain;

namespace eThangAgent.AgentDomain;

/// <summary>Optional per-turn stream callbacks. All members are null when the caller
///     wants no stream surface; providers without streaming simply never invoke them.</summary>
/// <param name="OnContentDelta">Invoked with each content fragment exactly as the
///     provider emits it — every iteration, interstitial text between tool calls
///     included. May fire on arbitrary threads; observers must marshal to their own
///     context.</param>
/// <param name="OnReasoningDelta">Invoked with each reasoning fragment the provider
///     streams.</param>
/// <param name="OnIterationEnd">Fires once after each provider response so observers can
///     separate iterations.</param>
/// <param name="OnToolCall">Invoked with (name, raw arguments, 1-based position in the
///     current provider response's tool batch, batch size) before each tool runs. A lone
///     call reports (name, args, 1, 1).</param>
/// <param name="OnToolResult">Invoked with (name, summarized result) after each tool
///     runs.</param>
/// <param name="OnContextUpdate">Invoked after each provider response (and after any
///     compaction) with the current context snapshot. May fire on arbitrary threads;
///     observers must marshal to their own context.</param>
/// <param name="OnCompacted">Invoked after a successful automatic compaction with what
///     the compactor did.</param>
public sealed record TurnCallbacks(
    Action<string>? OnContentDelta = null,
    Action<string>? OnReasoningDelta = null,
    Action? OnIterationEnd = null,
    Action<string, string, int, int>? OnToolCall = null,
    Action<string, string>? OnToolResult = null,
    Action<ContextSnapshot>? OnContextUpdate = null,
    Action<CompactionOutcome>? OnCompacted = null);
