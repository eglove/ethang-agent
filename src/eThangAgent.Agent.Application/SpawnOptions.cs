using eThangAgent.ModelDomain;

namespace eThangAgent.Agent.Application;

/// <summary>Model-resolution knobs for the spawn start command. Not a DI seam — plain
///     values grouped so the handler's constructor stays small. Members mirror the
///     former loose parameters exactly.</summary>
/// <param name="FallbackModelId">The host-injected fallback model: the resolution
///     chain's last resort when no explicit model, session preference, configured
///     default, or selection succeeds.</param>
/// <param name="Preferences">The session's live model choice (the host's model picker);
///     children follow it ahead of the static configured default.</param>
/// <param name="MaxTokens">Max tokens for the child's model config.</param>
/// <param name="Temperature">Temperature for the child's model config.</param>
/// <param name="ChildToolSurface">The parent's effective child tool surface (action ids);
///     grant validation measures requested allows against it. Null disables widening checks
///     (legacy wiring/tests only).</param>
/// <param name="WorkspaceRoot">The session workspace the handler validates a request's
///     anchor against when the parent record carries no persisted anchor of its own
///     (worktree ladder, T5). Composition supplies it; null means an anchored request
///     without a parent anchor is refused rather than silently inherited.</param>
/// <param name="ProviderName">The session's provider id ("openrouter"), stamped onto
///     every spawned child's persisted row so the Desktop subagent panel renders it and
///     diagnostics attribute the run (issue #124: all child rows carried NULL). Null
///     means legacy wiring supplies no provider — the row carries the named
///     UnboundProvider marker instead of SQL NULL, never a silent absence.</param>
public sealed record SpawnOptions(
    string FallbackModelId,
    SessionModelPreferences? Preferences = null,
    int MaxTokens = 4096,
    float Temperature = 0.7f,
    IReadOnlySet<string>? ChildToolSurface = null,
    string? WorkspaceRoot = null,
    string? ProviderName = null);
