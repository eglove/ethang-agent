namespace eThangAgent.ToolDomain;

/// <summary>Where a workspace's completion gate stands (issue #283). The states are
///     deliberately distinct because the REMEDY differs for each, and a single
///     'not fresh' would leave the model guessing.</summary>
public enum GoalGateStatus
{
  /// <summary>No <c>goal/check</c> is stored: this workspace declares no gate. NEVER
  ///     blocks - an unset gate is not a failed one, and the harness has no business
  ///     demanding a check the project never wrote.</summary>
  Unset,

  /// <summary>The gate exists but has never run in this workspace. Blocks: a completion
  ///     claim with no gate run behind it is exactly the record issue #283 was filed
  ///     about.</summary>
  NeverRan,

  /// <summary>The gate ran and failed. Blocks.</summary>
  Failed,

  /// <summary>The gate ran, passed, and the workspace content is unchanged since.
  ///     Allows.</summary>
  Fresh,

  /// <summary>The gate ran and passed, but the workspace content has changed since.
  ///     Blocks: a green run followed by five edits proves nothing about the code being
  ///     declared finished.</summary>
  Stale,

  /// <summary>Freshness cannot be decided - the workspace could not be read, or the
  ///     recorded run predates digest tracking. Does NOT block: refusing here would trap
  ///     the user with no exit, since re-running the gate cannot fix an unreadable tree.
  ///     The verdict says so plainly instead of pretending either way.</summary>
  Unverifiable,
}

/// <summary>The gate's answer, with the sentence that names the remedy. Rendered
///     verbatim by both attach points, so the model reads the same words wherever it
///     meets the refusal.</summary>
public readonly record struct CompletionGateVerdict(GoalGateStatus Status, string Message)
{
  /// <summary>True only for the states a completion claim must not pass. Unset and
  ///     Unverifiable deliberately do not block (see <see cref="GoalGateStatus"/>).</summary>
  public bool BlocksCompletion =>
      Status is GoalGateStatus.NeverRan or GoalGateStatus.Failed or GoalGateStatus.Stale;
}
