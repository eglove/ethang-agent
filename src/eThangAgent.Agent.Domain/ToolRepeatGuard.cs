namespace eThangAgent.AgentDomain;

/// <summary>Pure policy guarding against identical-failure loops: when the SAME tool
///     call (name + arguments, exact string identity) fails repeatedly in a row, the
///     model is stuck re-issuing a call it believes is correct — the harness must tell
///     it to change approach instead of watching the streak grow. Emits a nudge line at
///     the 2nd identical failure (correct the model before a third wasted call) and
///     once more at the 5th (covers models that ignore the first nudge). Any success or
///     a different call resets the streak. State is turn-local: the owner resets it at
///     each turn start.</summary>
public sealed class ToolRepeatGuard
{
  /// <summary>Consecutive identical failures that trip the first nudge.</summary>
  public const int FirstNudgeAt = 2;

  /// <summary>Consecutive identical failures that trip a second nudge.</summary>
  public const int SecondNudgeAt = 5;

  /// <summary>Longest error head quoted inside a nudge line.</summary>
  public const int MaxErrorHeadLength = 150;

  private string? _lastKey;
  private int _streak;

  /// <summary>Feeds one finished tool call. Returns the verbatim nudge line to inject
  ///     as a System message, or null when the call pattern is healthy. The line starts
  ///     with the "[repeat guard]" marker other loop-injected System messages use.</summary>
  public string? Observe(string name, string arguments, bool isError, string errorContent)
  {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(arguments);
    ArgumentNullException.ThrowIfNull(errorContent);
    string key = name + '\u001f' + arguments;
    if (_lastKey != key)
    {
      _lastKey = key;
      _streak = isError ? 1 : 0;
      return null;
    }

    if (!isError)
    {
      _streak = 0;
      return null;
    }

    _streak++;
    return _streak is FirstNudgeAt or SecondNudgeAt ? Format(name, _streak, errorContent) : null;
  }

  /// <summary>Clears the streak (turn start).</summary>
  public void Reset()
  {
    _lastKey = null;
    _streak = 0;
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
