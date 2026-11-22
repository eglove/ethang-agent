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
      "A message may name a file or a skill with @name, and the harness resolves each one to " +
      "what it names WITHOUT loading it: a file becomes its path, a skill becomes its name and " +
      "description. A [references] annotation on the message lists them; nothing in it is file " +
      "content or skill body. Fetch one only when you need it - you may already hold the file " +
      "or the skill from an earlier turn, and what you already have costs nothing to skip. A " +
      "file is read with the read tool and a skill with skill_view. A quoted reference " +
      "(@\"C:\\...\") also grants that path to the session, read-only, so the read tools reach " +
      "it for the rest of the session.";

  public string Build() => Guidance;
}
