using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;

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
/// <param name="OnToolResult">Invoked with (name, summarized result, full result content, error flag,
///     rich result) after each tool runs. The full content is exactly what entered the
///     conversation as the tool message; the rich result carries the tool's optional
///     display metadata (title, display body), null for tools without any.</param>
/// <param name="OnContextUpdate">Invoked after each provider response (and after any
///     compaction) with the current context snapshot. May fire on arbitrary threads;
///     observers must marshal to their own context.</param>
/// <param name="OnCompacted">Invoked after a successful automatic compaction with what
///     the compactor did.</param>
/// <param name="OnSystemMessage">Invoked verbatim whenever the loop itself appends a
///     System message to the conversation mid-turn (length-truncation continuation
///     prompt, compaction-failure notice), so hosts can surface loop-voice decisions
///     live. Also used by the application handler for post-turn nudge lines.</param>
public sealed record TurnCallbacks(
    Action<string>? OnContentDelta = null,
    Action<string>? OnReasoningDelta = null,
    Action? OnIterationEnd = null,
    Action<string, string, int, int>? OnToolCall = null,
    Action<string, string, string, bool, ToolResult?>? OnToolResult = null,
    Action<ContextSnapshot>? OnContextUpdate = null,
    Action<CompactionOutcome>? OnCompacted = null,
    Action<string>? OnSystemMessage = null);
