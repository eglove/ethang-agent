namespace eThangAgent.AgentDomain;

/// <summary>Pure policy guarding against loop shapes fed by repetition — both the
///     FAILURE shapes and the SUCCESS shape:
///     (1) the SAME tool call (name + arguments, exact string identity) failing
///         repeatedly in a row — the model is stuck re-issuing a call it believes is
///         correct (nudge at the 2nd identical failure, again at the 5th);
///     (2) the SAME tool failing across VARIED arguments (2026-10-04 incident: ~200
///         failed exec calls, each varied, so the identical-args nudges never re-fired)
///         — the cross-argument failure breaker (nudge at the 3rd, SUSPEND at the 13th);
///     (3) the SAME tool call SUCCEEDING with identical arguments repeatedly (2026-10-08
///         incident: a depth-1 subagent re-ran one identical exec call 235 times over
///         2.5 hours, every call returning the same 2843-char payload — invisible to a
///         guard fed only by isError) — the success-repetition breaker (nudge at the
///         3rd identical success, SUSPEND at the 13th).
///     Suspension refuses the tool for the turn's remainder (the loop enforces it).
///     Any success resets the failure streaks; a different call or different arguments
///     resets the success streak, so legitimate polling is not punished. State is
///     turn-local: the owner resets at turn start.</summary>
public sealed class ToolRepeatGuard
{
  /// <summary>Consecutive identical failures that trip the first nudge.</summary>
  public const int FirstNudgeAt = 2;

  /// <summary>Consecutive identical failures that trip a second nudge.</summary>
  public const int SecondNudgeAt = 5;

  /// <summary>Consecutive failures of one tool across ANY arguments that trip the
  ///     breaker nudge.</summary>
  public const int BreakerNudgeAt = 3;

  /// <summary>Consecutive failures of one tool that SUSPEND it for the turn's
  ///     remainder (the breaker's final escalation, at BreakerNudgeAt + 10).</summary>
  public const int SuspensionAt = BreakerNudgeAt + 10;

  /// <summary>Consecutive identical-argument SUCCESSES that trip the success-repetition
  ///     nudge.</summary>
  public const int SuccessNudgeAt = 3;

  /// <summary>Consecutive identical-argument SUCCESSES that SUSPEND the tool for the
  ///     turn's remainder (the success breaker's final escalation, mirroring the
  ///     failure breaker's SuspensionAt).</summary>
  public const int SuccessSuspensionAt = SuccessNudgeAt + 10;

  /// <summary>Longest error head quoted inside a nudge line.</summary>
  public const int MaxErrorHeadLength = 150;

  private string? _lastKey;
  private int _streak;
  private string? _breakerTool;
  private int _breakerStreak;
  private string? _successKey;
  private string? _successName;
  private int _successStreak;

  /// <summary>Feeds one finished tool call. Returns the verbatim nudge line to inject
  ///     as a System message, or null when the call pattern is healthy. The line starts
  ///     with the "[repeat guard]" marker other loop-injected System messages use.</summary>
  public string? Observe(string name, string arguments, bool isError, string errorContent)
  {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(arguments);
    ArgumentNullException.ThrowIfNull(errorContent);
    // The breakers observe EVERY call; the identical-args failure nudge (disjoint
    // thresholds: 2 and 5 vs the breakers' 3 and 13) takes precedence when both
    // would speak.
    string? breakerNudge = Breaker(name, isError);
    string key = name + '\u001f' + arguments;
    string? successNudge = Success(name, key, isError);
    if (_lastKey != key)
    {
      _lastKey = key;
      _streak = isError ? 1 : 0;
      return breakerNudge ?? successNudge;
    }

    if (!isError)
    {
      _streak = 0;
      return breakerNudge ?? successNudge;
    }

    _streak++;
    return _streak is FirstNudgeAt or SecondNudgeAt
        ? Format(name, _streak, errorContent)
        : breakerNudge ?? successNudge;
  }

  /// <summary>True when the tool is suspended for the turn's remainder: the loop
  ///     refuses further calls to it without executing. Suspension is armed by EITHER
  ///     breaker — the cross-argument failure breaker or the identical-argument success
  ///     repetition breaker.</summary>
  public bool IsSuspended(string name)
      => (_breakerTool == name && _breakerStreak >= SuspensionAt)
      || (_successName == name && _successStreak >= SuccessSuspensionAt);

  /// <summary>Clears all streaks and suspension (turn start).</summary>
  public void Reset()
  {
    _lastKey = null;
    _streak = 0;
    _breakerTool = null;
    _breakerStreak = 0;
    _successKey = null;
    _successName = null;
    _successStreak = 0;
  }

  /// <summary>The cross-argument failure breaker: tracks consecutive failures of one
  ///     tool across any arguments; a success or a different tool's call resets it.
  ///     Nudges at BreakerNudgeAt, suspends at SuspensionAt with escalated wording.</summary>
  private string? Breaker(string name, bool isError)
  {
    if (!isError)
    {
      _breakerTool = null;
      _breakerStreak = 0;
      return null;
    }

    if (_breakerTool != name)
    {
      _breakerTool = name;
      _breakerStreak = 1;
      return null;
    }

    _breakerStreak++;
    return _breakerStreak switch
    {
      BreakerNudgeAt => $"[repeat guard] Tool '{name}' has now failed {_breakerStreak} consecutive calls (any arguments). Stop using this tool for now: fix the arguments, re-read the tool's usage documentation, or take a different approach entirely.",
      SuspensionAt => $"[repeat guard] Tool '{name}' has now failed {_breakerStreak} consecutive calls (any arguments). This tool is suspended for the rest of this turn: further calls are refused without executing. Take a different approach.",
      _ => null,
    };
  }

  /// <summary>The success-repetition breaker (2026-10-08 incident): tracks consecutive
  ///     byte-identical SUCCESSFUL calls of one tool. A failure, a different tool, or
  ///     different arguments resets it — only exact-argument repetition counts, so
  ///     legitimate polling is not punished. Nudges at SuccessNudgeAt, suspends at
  ///     SuccessSuspensionAt with escalated wording.</summary>
  private string? Success(string name, string key, bool isError)
  {
    if (isError)
    {
      _successKey = null;
      _successName = null;
      _successStreak = 0;
      return null;
    }

    if (_successKey != key)
    {
      _successKey = key;
      _successName = name;
      _successStreak = 1;
      return null;
    }

    _successStreak++;
    return _successStreak switch
    {
      SuccessNudgeAt => $"[repeat guard] Tool call '{name}' has now returned the SAME result {_successStreak} consecutive times with identical arguments. The call and its result are unchanged: re-issuing it will not make progress. Take a different approach.",
      SuccessSuspensionAt => $"[repeat guard] Tool '{name}' has now returned the same result {_successStreak} consecutive times with identical arguments. This tool is suspended for the rest of this turn: further calls are refused without executing. Take a different approach.",
      _ => null,
    };
  }

  private static string Format(string name, int streak, string errorContent)
  {
    string head = errorContent.Split('\n')[0].TrimEnd('\r');
    if (head.Length > MaxErrorHeadLength)
    {
      head = head[..MaxErrorHeadLength] + "…";
    }

    return $"[repeat guard] Tool call '{name}' has failed {streak} consecutive times with the same error ({head}). "
        + "Do not repeat the call unchanged: fix the arguments, re-read the tool's usage documentation, or take a different approach.";
  }
}
