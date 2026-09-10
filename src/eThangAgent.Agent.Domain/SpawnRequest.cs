namespace eThangAgent.AgentDomain;

/// <summary>Request to spawn a child agent. TaskPrompt is required; Model falls back to the
///     configured default; Label is free text for humans and logs. Priority orders the
///     concurrency-boundary queue (higher wakes first; default inherits the parent's flow,
///     0); Contract carries the persisted spawn agreement (grants, ceilings, urgency).
///     WorkspaceRoot optionally anchors the child to a workspace directory (worktree
///     ladder, T5): null keeps the legacy path — no anchor validation, no contract change.
///     IsolateInWorktree asks the handler to provision a fresh worktree and anchor the
///     child at it (spawn-time worktree isolation): mutually exclusive with an explicit
///     WorkspaceRoot, and a provisioning failure refuses the spawn before it starts.</summary>
public sealed record SpawnRequest(string TaskPrompt, string? Model = null, string? Label = null,
    int Priority = 0, SpawnContract? Contract = null, string? WorkspaceRoot = null,
    bool IsolateInWorktree = false);
