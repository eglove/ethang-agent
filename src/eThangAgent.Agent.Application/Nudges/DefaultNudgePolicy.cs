namespace eThangAgent.Agent.Application.Nudges;

/// <summary>
/// Reminds the model to curate memories after tool-heavy turns that produced no writes.
/// Fires only when every condition holds: the turn is a multiple of five, at least three
/// tool calls were executed in it, and zero curated memories have been written this session.
/// The clock seam is accepted for signature stability with future time-aware policies;
/// the current rule conditions purely on turn boundaries (no polling, no timers).
/// </summary>
public sealed class DefaultNudgePolicy(Func<DateTimeOffset> clock) : INudgePolicy
{
#pragma warning disable IDE0051 // Remove unread private member
  private Func<DateTimeOffset> ClockUnused => clock; // retained for API compatibility
#pragma warning restore IDE0051
  /// <summary>The exact line appended to the conversation when the policy fires.</summary>
  public const string ReminderLine =
      "[nudge] This turn involved several tools and nothing has been saved to curated memories yet. " +
      "If any durable convention, preference, insight, failure, or reference emerged, consider " +
      "memories.add — otherwise continue.";

  public string? Evaluate(NudgeContext context)
  {
    ArgumentNullException.ThrowIfNull(context);
    return context.TurnNumber % 5 == 0
        && context.LastToolCalls >= 3
        && context.MemoriesWrittenTotal == 0
        ? ReminderLine
        : null;
  }
}
