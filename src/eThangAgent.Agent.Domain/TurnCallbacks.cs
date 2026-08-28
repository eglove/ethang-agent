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
/// <param name="OnToolCall">Invoked with (name, raw arguments) before each tool runs.</param>
/// <param name="OnToolResult">Invoked with (name, summarized result) after each tool
///     runs.</param>
public sealed record TurnCallbacks(
    Action<string>? OnContentDelta = null,
    Action<string>? OnReasoningDelta = null,
    Action? OnIterationEnd = null,
    Action<string, string>? OnToolCall = null,
    Action<string, string>? OnToolResult = null);
