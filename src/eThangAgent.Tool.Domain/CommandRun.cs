namespace eThangAgent.ToolDomain;

/// <summary>One completed user command run (the ! chat command): the assigned
///     workspace-scoped id, the command text, its exit code, whether the budget
///     expired (partial output), the captured output, and when it ran.</summary>
public sealed record CommandRun(
    int Id,
    string Command,
    int ExitCode,
    bool TimedOut,
    string Output,
    DateTimeOffset RanAt)
{
  /// <summary>The compact model-facing line appended to the session conversation:
  ///     what ran, its id, and how it ended - success, exit code, or timeout.
  ///     Points the model at command_output for the stored output; the output
  ///     itself never rides this message. Rendered here so every surface (the
  ///     conversation append and the live transcript) shows the identical text.</summary>
  public string ModelFacingLine
  {
    get
    {
      string outcome = TimedOut
          ? "timed out (partial output captured)"
          : $"exit code {ExitCode}";
      return $"User ran: `{Command}` (id: {Id}, {outcome}). " +
          "Output is stored — call command_output to read it if the user references it.";
    }
  }
}
