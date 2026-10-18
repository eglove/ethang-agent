namespace eThangAgent.AgentDomain;

/// <summary>Structured outcome of one <see cref="ToolRepeatGuard.Observe"/> call.
///     <see cref="NudgeLine"/> is the verbatim System line to inject (null = quiet);
///     <see cref="Critical"/> marks the breaker's critical error (the model must address
///     it); <see cref="EndTurn"/> marks the call AFTER a critical: the loop stops the
///     turn. Suspension is deleted — no code path may suspend a tool again.</summary>
public sealed record GuardOutcome(string? NudgeLine, bool Critical, bool EndTurn)
{
  /// <summary>The quiet outcome: no nudge, nothing critical, the turn continues.</summary>
  public static GuardOutcome Quiet { get; } = new(null, false, false);
}

/// <summary>Pure policy guarding against loop shapes fed by repetition — both the
///     FAILURE shapes and the SUCCESS shape:
///     (1) the SAME tool call (name + arguments, exact string identity) failing
///         repeatedly in a row — the model is stuck re-issuing a call it believes is
///         correct (nudge at the 2nd identical failure, again at the 5th);
///     (2) the SAME tool failing across VARIED arguments (2026-10-04 incident: ~200
///         failed exec calls, each varied, so the identical-args nudges never re-fired)
///         — the cross-argument failure breaker (nudge at the 3rd, critical at the 13th);
///     (3) the SAME tool call SUCCEEDING with identical arguments repeatedly (2026-10-08
///         incident: a depth-1 subagent re-ran one identical exec call 235 times over
///         2.5 hours, every call returning the same 2843-char payload — invisible to a
///         guard fed only by isError) — the success-repetition breaker (nudge at the
///         3rd identical success, critical at the 13th).
///     At the 13th breaker-counted call the guard escalates to a CRITICAL error the
///     model must address; the NEXT breaker-counted call sets EndTurn and the loop ends
///     the turn. Any success resets the failure streaks; a different call or different
///     arguments resets the success streak, so legitimate polling is not punished.
///     State is turn-local: the owner resets at turn start.</summary>
public sealed class ToolRepeatGuard
{
  /// <summary>Consecutive identical failures that trip the first nudge.</summary>
  public const int FirstNudgeAt = 2;

  /// <summary>Consecutive identical failures that trip a second nudge.</summary>
  public const int SecondNudgeAt = 5;

  /// <summary>Consecutive failures of one tool across ANY arguments that trip the
  ///     breaker nudge.</summary>
  public const int BreakerNudgeAt = 3;

  /// <summary>Consecutive failures of one tool that escalate to the CRITICAL error
  ///     (the breaker's final warning before the turn ends, at BreakerNudgeAt + 10).</summary>
  public const int BreakerCriticalAt = BreakerNudgeAt + 10;

  /// <summary>Consecutive identical-argument SUCCESSES that trip the success-repetition
  ///     nudge.</summary>
  public const int SuccessNudgeAt = 3;

  /// <summary>Consecutive identical-argument SUCCESSES that escalate to the CRITICAL
  ///     error (the success breaker's final warning, mirroring the failure breaker's
  ///     BreakerCriticalAt).</summary>
  public const int SuccessCriticalAt = SuccessNudgeAt + 10;

  /// <summary>Longest error head quoted inside a nudge line.</summary>
  public const int MaxErrorHeadLength = 150;

  private string? _lastKey;
  private int _streak;
  private string? _breakerTool;
  private int _breakerStreak;
  private bool _breakerCriticalFired;
  private string? _successKey;
  private int _successStreak;
  private bool _successCriticalFired;

  /// <summary>Feeds one finished tool call. Returns the structured outcome: the verbatim
  ///     nudge line to inject as a System message (the line starts with the
  ///     "[repeat guard]" marker other loop-injected System messages use), the critical
  ///     flag at the breaker threshold, and EndTurn on the call after a critical.</summary>
  public GuardOutcome Observe(string name, string arguments, bool isError, string errorContent)
  {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(arguments);
    ArgumentNullException.ThrowIfNull(errorContent);
    // The breakers observe EVERY call; the identical-args failure nudge (disjoint
    // thresholds: 2 and 5 vs the breakers' 3 and 13) takes precedence when both
    // would speak.
    GuardOutcome breaker = Breaker(name, isError);
    string key = name + '\u001f' + arguments;
    GuardOutcome success = Success(name, key, isError);
    GuardOutcome outcome = Combine(breaker, success);
    if (_lastKey != key)
    {
      _lastKey = key;
      _streak = isError ? 1 : 0;
      return outcome;
    }

    if (!isError)
    {
      _streak = 0;
      return outcome;
    }

    _streak++;
    return _streak is FirstNudgeAt or SecondNudgeAt
        ? new GuardOutcome(Format(name, _streak, errorContent), false, false)
        : outcome;
  }

  /// <summary>Precedence when both breakers speak on one call: a terminal or critical
  ///     outcome wins over a plain nudge; the failure breaker wins over the success
  ///     breaker (disjoint thresholds make simultaneous nudges rare, but the order is
  ///     pinned).</summary>
  private static GuardOutcome Combine(GuardOutcome breaker, GuardOutcome success)
  {
    if (breaker.EndTurn)
    {
      return breaker;
    }

    if (success.EndTurn)
    {
      return success;
    }

    if (breaker.Critical)
    {
      return breaker;
    }

    if (success.Critical)
    {
      return success;
    }

    if (breaker.NudgeLine is { } breakerLine)
    {
      return new GuardOutcome(breakerLine, false, false);
    }

    GuardOutcome successNudge = success.NudgeLine is { } successLine
        ? new GuardOutcome(successLine, false, false)
        : GuardOutcome.Quiet;
    return successNudge;
  }

  /// <summary>Clears all streaks and the critical-fired tracking (turn start).</summary>
  public void Reset()
  {
    _lastKey = null;
    _streak = 0;
    _breakerTool = null;
    _breakerStreak = 0;
    _breakerCriticalFired = false;
    _successKey = null;
    _successStreak = 0;
    _successCriticalFired = false;
  }

  /// <summary>The cross-argument failure breaker: tracks consecutive failures of one
  ///     tool across any arguments; a success or a different tool's call resets it.
  ///     Nudges at BreakerNudgeAt, escalates to CRITICAL at BreakerCriticalAt, and sets
  ///     EndTurn on the next breaker-counted call after the critical fired.</summary>
  private GuardOutcome Breaker(string name, bool isError)
  {
    if (!isError)
    {
      _breakerTool = null;
      _breakerStreak = 0;
      _breakerCriticalFired = false;
      return GuardOutcome.Quiet;
    }

    if (_breakerTool != name)
    {
      _breakerTool = name;
      _breakerStreak = 1;
      _breakerCriticalFired = false;
      return GuardOutcome.Quiet;
    }

    _breakerStreak++;
    if (_breakerStreak == BreakerCriticalAt)
    {
      _breakerCriticalFired = true;
      return new GuardOutcome(CriticalLine(name), Critical: true, EndTurn: false);
    }

    if (_breakerCriticalFired)
    {
      return new GuardOutcome(null, Critical: false, EndTurn: true);
    }

    GuardOutcome breakerNudge = _breakerStreak == BreakerNudgeAt
        ? new GuardOutcome($"[repeat guard] Tool '{name}' has now failed {_breakerStreak} consecutive calls (any arguments). Stop using this tool for now: fix the arguments, re-read the tool's usage documentation, or take a different approach entirely.", false, false)
        : GuardOutcome.Quiet;
    return breakerNudge;
  }

  /// <summary>The success-repetition breaker (2026-10-08 incident): tracks consecutive
  ///     byte-identical SUCCESSFUL calls of one tool. A failure, a different tool, or
  ///     different arguments resets it — only exact-argument repetition counts, so
  ///     legitimate polling is not punished. Nudges at SuccessNudgeAt, escalates to
  ///     CRITICAL at SuccessCriticalAt, and sets EndTurn on the next breaker-counted
  ///     call after the critical fired.</summary>
  private GuardOutcome Success(string name, string key, bool isError)
  {
    if (isError)
    {
      _successKey = null;
      _successStreak = 0;
      _successCriticalFired = false;
      return GuardOutcome.Quiet;
    }

    if (_successKey != key)
    {
      _successKey = key;
      _successStreak = 1;
      _successCriticalFired = false;
      return GuardOutcome.Quiet;
    }

    _successStreak++;
    if (_successStreak == SuccessCriticalAt)
    {
      _successCriticalFired = true;
      return new GuardOutcome(CriticalLine(name), Critical: true, EndTurn: false);
    }

    if (_successCriticalFired)
    {
      return new GuardOutcome(null, Critical: false, EndTurn: true);
    }

    GuardOutcome successNudge = _successStreak == SuccessNudgeAt
        ? new GuardOutcome($"[repeat guard] Tool call '{name}' has now returned the SAME result {_successStreak} consecutive times with identical arguments. The call and its result are unchanged: re-issuing it will not make progress. Take a different approach.", false, false)
        : GuardOutcome.Quiet;
    return successNudge;
  }

  private static string CriticalLine(string name) =>
      $"[repeat guard] CRITICAL: tool '{name}' has now repeated the same call 13 consecutive times. This is a critical error that must be addressed: stop, reconsider your approach, and change what you do next. Continuing unchanged will end your turn.";

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
