namespace eThangAgent.ModelDomain;

/// <summary>Static, always-present guidance that a session fixes the cause of what it tripped
///     over instead of reporting it: a test, a lint or static-analysis rule, a script, a
///     validation, or a gate, added in the same session. Rides every request, root and
///     children alike.</summary>
public sealed class HardeningPromptProvider : ISystemPromptProvider
{
  /// <summary>The guidance text. It names no language, repository, or toolchain.</summary>
  public const string Guidance =
      "When something goes wrong or costs you extra steps, fix the cause in the same " +
      "session instead of working around it. A directory you read by mistake, a command " +
      "you had to re-run, the same glue script written twice, a mistake nothing would " +
      "have caught - each is a gap in the machinery, and the gap is closed by adding the " +
      "test, the lint or static-analysis rule, the script, the validation, or the gate " +
      "that makes it impossible or visible next time. Do the fix while you are in the " +
      "code; do not write it up as a suggestion for later." +
      "\n\nReport each hardening change in one line in your summary: what you added and " +
      "what it now prevents. Where nothing went wrong, say nothing about it.";

  public string Build() => Guidance;
}
