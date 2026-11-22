using eThangAgent.ModelDomain;

namespace eThangAgent.Composition;

/// <summary>Static, always-present guidance on what an <c>@</c> reference in a user message is:
///     a pointer the user wrote, not content the harness fetched. Reading it is the model's
///     decision, because the file may already be in context and a read the model did not need
///     is the user's tokens spent twice. Rides every request, root and children alike.</summary>
public sealed class ReferencesPromptProvider : ISystemPromptProvider
{
  /// <summary>The guidance text. It names the annotation the composer appends, so a model that
  ///     receives one knows where the paths came from.</summary>
  public const string Guidance =
      "A message may name files with @path, and the harness resolves each one to the path it " +
      "points at without reading it. A [references] annotation on the message lists those " +
      "paths; nothing in it is file content. Read one only when you need what is inside it - " +
      "you may already hold the file from an earlier turn, and a file you have already read " +
      "costs nothing to skip. A quoted reference (@\"C:\\...\") also grants that path to the " +
      "session, read-only, so the read tools reach it for the rest of the session.";

  public string Build() => Guidance;
}
