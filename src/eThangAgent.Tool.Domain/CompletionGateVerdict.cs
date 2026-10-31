namespace eThangAgent.ToolDomain;

/// <summary>Where a workspace's completion gate stands (issue #283). The states are
///     deliberately distinct because the REMEDY differs for each, and a single
///     'not fresh' would leave the model guessing.</summary>
public enum GoalGateStatus
{
  /// <summary>No <c>goal/check</c> is stored and no decline is recorded: nobody has ever
  ///     ASKED this workspace whether it wants a gate. NEVER blocks - an unset gate is not a
  ///     failed one, and the harness has no business demanding a check the project never wrote.
  ///     <para>DISTINCT FROM <see cref="Declined"/> (issue #285 D1): 'nobody asked' and 'the user
  ///     said no' are different facts, and collapsing them would make a recorded decision
  ///     indistinguishable from an omission. The difference is user-visible in the verdict
  ///     message and in <c>state.checkgoal</c>'s report.</para></summary>
  Unset,

  /// <summary>The user was asked and DECLINED a gate (issue #285 D1), recorded at
  ///     <c>goal/declined</c>. NEVER blocks, and is never asked again.
  ///     <para>A DECLINE IS A DECISION, not an absence - the user's words were "a user decision
  ///     for 'null' is still a user decision". Keeping it separate is what lets the harness say
  ///     'you declined a gate here' rather than 'no gate exists', so a declined workspace is
  ///     auditable instead of looking like one nobody configured.</para>
  ///     <para>Stored as a SIBLING key, never a sentinel inside <c>goal/check</c>: an empty array
  ///     there already means BROKEN CONFIG to the checkgoal runner, and overloading it would
  ///     conflate a deliberate choice with a fault.</para></summary>
  Declined,

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
  /// <summary>True only for the states a completion claim must not pass. Unset, Declined and
  ///     Unverifiable deliberately do not block (see <see cref="GoalGateStatus"/>): a workspace
  ///     that never configured a gate, or whose user declined one, has nothing to be checked
  ///     against, and refusing there would demand a check the project never wrote.</summary>
  public bool BlocksCompletion =>
      Status is GoalGateStatus.NeverRan or GoalGateStatus.Failed or GoalGateStatus.Stale;

  /// <summary>True when this workspace has NO gate, by a stated decision or by omission - the
  ///     two cases a reporting surface must render as 'no gate configured' rather than as a
  ///     failure (issue #285).
  ///     <para>WHY THIS EXISTS: <c>state.checkgoal</c> reported an unset gate as
  ///     <c>Violated: true</c>, contradicting the rule that it never blocks. A surface that
  ///     renders an absent gate as a violation tells the user something failed when nothing was
  ///     ever configured - the same error as showing a cost of zero for spend nobody measured.
  ///     </para></summary>
  public bool HasNoGate => Status is GoalGateStatus.Unset or GoalGateStatus.Declined;
}
