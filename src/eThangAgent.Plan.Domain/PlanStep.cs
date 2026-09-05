namespace eThangAgent.PlanDomain;

/// <summary>One ordered step of a plan: 1-based position, title, optional detail, todo link.</summary>
public sealed record PlanStep(int Position, string Title, string? Detail, int? TodoId, PlanStepStatus Status)
{
  /// <summary>Creates a new step in the default Pending state; trims the title, rejects empties.</summary>
  public static PlanStep New(int position, string title, string? detail, int? todoId)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(title);
    string trimmed = title.Trim();
    return todoId is < 1
      ? throw new ArgumentException("TodoId must be at least 1.", nameof(todoId), null)
      : new PlanStep(position, trimmed, detail, todoId, PlanStepStatus.Pending);
  }
}
