namespace eThangAgent.Agent.Application.Nudges;

/// <summary>Decides whether a completed turn deserves a reminder appended to the conversation.</summary>
public interface INudgePolicy
{
  /// <returns>The reminder line to append as a System message, or null when silent.</returns>
  string? Evaluate(NudgeContext context);
}
