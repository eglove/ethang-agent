namespace eThangAgent.AgentDomain;

/// <summary>Which chain step a session was seeded for (issue #301 D6). The harness stamps one
///     on every session it starts, and the ownership check reads it: a completion naming a
///     step this record does not name is refused.
///     <para>This is a VIEW over the chain record, never a second store. The chain step
///     already carries the claiming <c>SessionId</c> (issue #300), so the assignment is
///     <c>chain.StepForSession(sessionId)</c> - duplicating it into a row of its own would
///     create two answers to one question that could disagree after a re-claim.</para>
///     <para><paramref name="SessionId"/> is nullable because the launcher is handed the
///     assignment BEFORE the session exists: the harness starts the session, then claims the
///     step for it. A record whose session is unknown still names the step.</para></summary>
/// <param name="ChainId">The chain the step belongs to.</param>
/// <param name="PlanId">The plan the chain executes; the step is named by position in it.</param>
/// <param name="Position">The 1-based chain step position the session is assigned.</param>
/// <param name="SessionId">The session holding the assignment; null before it is created.</param>
public sealed record ChainStepAssignment(int ChainId, int PlanId, int Position, string? SessionId)
{
  /// <summary>True when this assignment names a position - the only shape a launcher can act on.</summary>
  public bool IsExecutable => ChainId >= 1 && PlanId >= 1 && Position >= 1;

  public override string ToString()
      => string.Create(System.Globalization.CultureInfo.InvariantCulture,
          $"chain #{ChainId} step {Position} of plan #{PlanId}");
}
