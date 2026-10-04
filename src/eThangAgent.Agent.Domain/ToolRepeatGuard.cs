namespace eThangAgent.AgentDomain;

/// <summary>Pure policy guarding against failure loops, both shapes: (1) the SAME tool
///     call (name + arguments, exact string identity) failing repeatedly in a row — the
///     model is stuck re-issuing a call it believes is correct; (2) the SAME tool
///     failing across VARIED arguments (2026-10-04 incident: ~200 failed exec calls,
///     each varied, so the identical-args nudges never re-fired). Emits a nudge line at
///     the 2nd identical failure and again at the 5th; the cross-argument breaker
///     nudges at the 3rd consecutive failure of one tool and SUSPENDS the tool at the
///     13th (3 + 10), after which the loop refuses it for the turn's remainder. Any
///     success resets both streaks. State is turn-local: the owner resets at turn start.</summary>
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

  /// <summary>Longest error head quoted inside a nudge line.</summary>
  public const int MaxErrorHeadLength = 150;

  private string? _lastKey;
  private int _streak;
  private string? _breakerTool;
  private int _breakerStreak;

  /// <summary>Feeds one finished tool call. Returns the verbatim nudge line to inject
  ///     as a System message, or null when the call pattern is healthy. The line starts
  ///     with the "[repeat guard]" marker other loop-injected System messages use.</summary>
  public string? Observe(string name, string arguments, bool isError, string errorContent)
  {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(arguments);
    ArgumentNullException.ThrowIfNull(errorContent);
    // The breaker observes EVERY call; the identical-args nudge (disjoint
    // thresholds: 2 and 5 vs the breaker's 3 and 13) takes precedence when both
    // would speak.
    string? breakerNudge = Breaker(name, isError);
    string key = name + '\u001f' + arguments;
    if (_lastKey != key)
    {
      _lastKey = key;
      _streak = isError ? 1 : 0;
      return breakerNudge;
    }

    if (!isError)
    {
      _streak = 0;
      return breakerNudge;
    }

    _streak++;
    return _streak is FirstNudgeAt or SecondNudgeAt ? Format(name, _streak, errorContent) : breakerNudge;
  }

  /// <summary>True when the tool is suspended for the turn's remainder: the loop
  ///     refuses further calls to it without executing.</summary>
  public bool IsSuspended(string name) => _breakerTool == name && _breakerStreak >= SuspensionAt;

  /// <summary>Clears all streaks and suspension (turn start).</summary>
  public void Reset()
  {
    _lastKey = null;
    _streak = 0;
    _breakerTool = null;
    _breakerStreak = 0;
  }

  /// <summary>The cross-argument breaker: tracks consecutive failures of one tool
  ///     across any arguments; a success or a different tool's call resets it. Nudges
  ///     at BreakerNudgeAt, suspends at SuspensionAt with escalated wording.</summary>
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
