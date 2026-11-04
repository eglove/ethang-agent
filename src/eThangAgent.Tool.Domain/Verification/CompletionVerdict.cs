namespace eThangAgent.ToolDomain.Verification;

/// <summary>Where a session's verification stands when a completion claim is made.</summary>
public enum VerificationGateStatus
{
  /// <summary>A command exited 0 after the newest workspace change.</summary>
  Fresh,

  /// <summary>No command has exited 0 in this session.</summary>
  NeverRan,

  /// <summary>Commands ran, but none exited 0; the newest one's code is named in the message.</summary>
  Failed,

  /// <summary>A command exited 0, but the workspace changed after it started.</summary>
  Stale,

  /// <summary>The workspace could not be read in full, so freshness cannot be decided.</summary>
  Unverifiable,
}

/// <summary>The completion gate's answer, with the sentence that names the remedy.</summary>
public readonly record struct CompletionVerdict(VerificationGateStatus Status, string Message)
{
  /// <summary>True only for the states a completion claim must not pass. An unverifiable
  ///     workspace does not block: re-running verification cannot fix an unreadable tree.</summary>
  public bool BlocksCompletion =>
      Status is VerificationGateStatus.NeverRan or VerificationGateStatus.Failed
          or VerificationGateStatus.Stale;
}
