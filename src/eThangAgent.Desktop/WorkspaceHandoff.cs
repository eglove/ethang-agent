using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop;

/// <summary>The workspace a launch chose, carried from the launcher window to the shell.
///     <para>The root is normalized through <see cref="WorkspaceIdentity.Normalize"/> at
///     construction, so every surface downstream compares one spelling of one directory. A
///     blank root is a programmer error rather than an expected failure: the launcher only
///     produces a handoff for a directory the user picked and the picker returned.</para></summary>
internal sealed record WorkspaceHandoff
{
  /// <summary>The absolute, normalized directory this launch chose.</summary>
  public string WorkspaceRoot { get; }

  public WorkspaceHandoff(string workspaceRoot)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
    WorkspaceRoot = WorkspaceIdentity.Normalize(workspaceRoot);
  }
}
