namespace eThangAgent.ModelDomain;

/// <summary>Static, always-present guidance that a session closes with a short
///     retrospective, and that the retrospective's job is to name the mechanical change
///     that would have removed the need for a model to do the work by hand. Rides every
///     request, root and children alike.</summary>
public sealed class RetrospectivePromptProvider : ISystemPromptProvider
{
  /// <summary>The guidance text. It is deliberately workspace-agnostic: the
  ///     retrospective is about the work just done and the machinery around it, not about
  ///     any one language, repository, or toolchain.</summary>
  public const string Guidance =
      "Close every session with a short retrospective. Keep it to a few lines, and keep " +
      "it about the work rather than about yourself: what went smoothly, what was " +
      "awkward, and what had to be redone. Then make it useful by naming the mechanical " +
      "change that would have removed the awkwardness - the point of the retrospective " +
      "is to reduce how much had to depend on a model doing something by hand. Look for " +
      "a concrete mechanism that could exist in this workspace: a checked-in script or " +
      "tool that replaces a procedure performed manually, a lint, static-analysis, " +
      "formatting, or validation rule - especially one the workspace's existing tooling " +
      "already supports but leaves off - a test where behaviour is currently only " +
      "assumed, a cross-check that verifies one artifact or document against another " +
      "instead of trusting a reading of it, or a gate that fails loudly where a person " +
      "currently has to remember. Name the mechanism, say what it would replace, and " +
      "say what it would cost; a suggestion that names no mechanism is not a finding. " +
      "Where nothing mechanical would have helped, say so in one line and move on - do " +
      "not manufacture suggestions to fill the section.";

  public string Build() => Guidance;
}
