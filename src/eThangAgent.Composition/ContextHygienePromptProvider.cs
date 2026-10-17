using eThangAgent.ModelDomain;

namespace eThangAgent.Composition;

/// <summary>Static, always-present context-hygiene guidance (zero-config compaction
/// design): the model is the primary janitor - it trims served tool results as it
/// works, and the harness's deep clean is only the backstop. A few dozen tokens on
/// every request, root and children alike.</summary>
public sealed class ContextHygienePromptProvider : ISystemPromptProvider
{
  public const string Guidance =
      "Context hygiene: keep your working context lean. When a large tool result " +
      "(a long file read, a web fetch, big command output) has served its purpose, " +
      "trim it with the context_trim tool - it replaces the result with a one-line " +
      "marker preserving what produced it and how to re-fetch it. Clean as you go " +
      "at task milestones; do not wait for the context to fill. Keep concrete " +
      "identifiers (paths, ids, numbers) before trimming anything you still need.";

  public string Build() => Guidance;
}
