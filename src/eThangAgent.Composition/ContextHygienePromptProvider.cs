using eThangAgent.ModelDomain;

namespace eThangAgent.Composition;

/// <summary>Static, always-present context-hygiene guidance (zero-config compaction
/// design): the model is the primary janitor - it trims served tool results as it
/// works, and the harness's deep clean is only the backstop. A few dozen tokens on
/// every request, root and children alike.</summary>
public sealed class ContextHygienePromptProvider : ISystemPromptProvider
{
  /// <summary>THE GUIDANCE MUST NAME A DOOR THE MODEL ACTUALLY HAS. Since issue #288 the
  ///     loop registry holds exactly one tool (exec) and every other capability is reached
  ///     as <c>Tools.*</c> INSIDE an exec program - but this text predated that collapse and
  ///     said "trim it with the context_trim tool", which reads as a top-level call. A model
  ///     that followed it got <c>Error [UnknownTool]</c> for the very verb the harness had
  ///     just asked it to use, then guessed at parameters instead of trimming. The wording is
  ///     pinned by <c>ContextHygienePromptTests.Guidance_PointsAtADoorTheModelActuallyHas</c>.</summary>
  public const string Guidance =
      "Context hygiene: keep your working context lean. When a large tool result " +
      "(a long file read, a web fetch, big command output) has served its purpose, " +
      "trim it with Tools.context_trim inside an exec program - it replaces the result " +
      "with a one-line marker preserving what produced it and how to re-fetch it. " +
      "Clean as you go at task milestones; do not wait for the context to fill. Keep " +
      "concrete identifiers (paths, ids, numbers) before trimming anything you still need.";

  public string Build() => Guidance;
}
