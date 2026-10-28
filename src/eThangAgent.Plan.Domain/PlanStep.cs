namespace eThangAgent.PlanDomain;

/// <summary>One ordered step of a plan: 1-based position, title, optional detail, and status.
///     <para>The former <c>TodoId</c> link is gone (issue #310): the todo list it pointed at
///     was retired, and a column that can only ever be null is a permanent reminder of a
///     deleted concept. The plan step's shape now says what it means.</para></summary>
public sealed record PlanStep(int Position, string Title, string? Detail, PlanStepStatus Status)
{
  /// <summary>Creates a new step in the default Pending state; trims the title, rejects empties.</summary>
  public static PlanStep New(int position, string title, string? detail)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(title);
    return new PlanStep(position, title.Trim(), detail, PlanStepStatus.Pending);
  }
}
