using System.Globalization;
using eThangAgent.AgentDomain;

namespace eThangAgent.Composition;

/// <summary>Strict composition-root binding of SubAgent configuration into
///     <see cref="SubAgentOptions"/>. Keys:
///     "SubAgent:DefaultModel" — optional; absent is legal (spawns must then pass a
///     model explicitly); present-but-empty is a startup validation error.
///     "SubAgent:MaxConcurrentAgents" — required positive integer; absent,
///     non-integer, or below-1 values are startup validation errors.
///     "SubAgent:Watchdog:TickInterval" / ":IdleThreshold" — optional constant-format
///     (":"-separated) durations; absent keeps the default, empty, non-parseable,
///     non-positive, or bare-integer (a silent days value) text is a startup error.
///     "SubAgent:Watchdog:MaxWrapUpAttempts" — optional non-negative integer; same rule.
///     "SubAgent:RootTurn:Idle" — an optional constant-format duration for the app-side
///     root-turn budget; an absent key keeps the default, and invalid or non-positive text
///     is a startup error (never clamped). There is deliberately no ceiling key (issue #248):
///     the total-duration bound is gone, so a stored "SubAgent:RootTurn:Ceiling" is simply
///     not bound.
///     Nothing is silently coerced or clamped. There is deliberately no child-timeout
///     key (FR-L4/A4): wall-clock is never a child cancellation source.</summary>
public static class SubAgentConfiguration
{
  public static SubAgentOptions Bind(string? defaultModel, string? maxConcurrentAgents,
      string? rootTurnIdle = null)
  {
    ValidateDefaultModel(defaultModel);
    int maxConcurrent = ParseMaxConcurrentAgents(maxConcurrentAgents);
    RootTurnBudget? rootTurn = BindRootTurnBudget(rootTurnIdle);
    return new SubAgentOptions(defaultModel, MaxConcurrentAgents: maxConcurrent,
        RootTurnBudget: rootTurn);
  }

  /// <summary>"SubAgent:RootTurn:Idle" — the app-side root-turn budget's ONE knob. Absent
  ///     keeps the runtime default; invalid or non-positive text is a startup error, never
  ///     clamped. The idle window is the only bound that needs configuring (issue #248): the
  ///     send bound defaults sensibly and there is no total-duration ceiling any more.</summary>
  private static RootTurnBudget? BindRootTurnBudget(string? idleValue)
  {
    if (idleValue is null)
    {
      return null;
    }

    TimeSpan idle = ParseRootTurnDuration(idleValue, "SubAgent:RootTurn:Idle");
    if (idle < MinimumRootTurnIdle)
    {
      // The host HEARTBEATS inside this window (a progress event whenever the turn is
      // quiet), and the heartbeat's shortest interval is one second - so a window under
      // four seconds would hold fewer than four heartbeats, and the app would read a
      // HEALTHY host as silent. A configuration that guarantees a false failure is a
      // startup error, never a silent clamp (2026-10-22).
      throw new InvalidOperationException(
          $"SubAgent:RootTurn:Idle ({idleValue}) must be at least {MinimumRootTurnIdle} "
          + "(the conversation host heartbeats inside this window, and a shorter one would fail healthy turns).");
    }

    return new RootTurnBudget(idle, RootTurnBudget.Default.Send);
  }

  /// <summary>The shortest root-turn idle window the harness accepts: four times the
  ///     heartbeat's shortest interval, so the liveness signal always arrives several
  ///     times inside the window (2026-10-22).</summary>
  public static readonly TimeSpan MinimumRootTurnIdle = TimeSpan.FromSeconds(4);

  /// <summary>One root-turn duration: constant format only, positive, no bare integers.</summary>
  private static TimeSpan ParseRootTurnDuration(string value, string key)
  {
    if (value.Length == 0)
    {
      throw new InvalidOperationException(
          $"{key} is present but empty. Remove the key or supply a positive duration (e.g. '00:02:00').");
    }

    // A bare integer would parse (TimeSpan reads it as DAYS) — rejected, never coerced.
    if (value.All(char.IsDigit))
    {
      throw new InvalidOperationException(
          $"{key} must carry units (e.g. '00:02:00'); the bare integer '{value}' would bind as days.");
    }

    bool parsed = TimeSpan.TryParseExact(value, ["c", "g"], CultureInfo.InvariantCulture,
        TimeSpanStyles.None, out TimeSpan duration);
    return parsed && duration > TimeSpan.Zero
        ? duration
        : throw new InvalidOperationException(
            $"{key} must be a positive duration in constant format (e.g. '00:02:00'), got '{value}'.");
  }

  /// <summary>"SubAgent:Watchdog:*" (W1.2) — the host-watchdog knobs, bound strictly:
  ///     absent keys keep the watchdog defaults (the 15-minute idle threshold and
  ///     friends); invalid text aborts startup, never clamped. Bare integers are
  ///     rejected even though TimeSpan would parse them (as DAYS) — a misread that
  ///     large is never coerced (named decision).</summary>
  public static WatchdogSettings? BindWatchdog(
      string? tickInterval, string? idleThreshold, string? maxWrapUpAttempts)
  {
    TimeSpan? tick = ParseWatchdogDuration(tickInterval, "SubAgent:Watchdog:TickInterval");
    TimeSpan? idle = ParseWatchdogDuration(idleThreshold, "SubAgent:Watchdog:IdleThreshold");
    return tick is null && idle is null && maxWrapUpAttempts is null
        ? null // no watchdog configuration at all: the host watchdog stays fully default
        : new WatchdogSettings(tick, idle, ParseWatchdogAttempts(maxWrapUpAttempts));
  }

  private static TimeSpan? ParseWatchdogDuration(string? value, string key)
  {
    if (value is null)
    {
      return null;
    }

    if (value.Length == 0)
    {
      throw new InvalidOperationException(
          $"{key} is present but empty. Remove the key or supply a positive duration (e.g. '00:00:02').");
    }

    // A bare integer would parse (TimeSpan reads it as DAYS) — rejected, never coerced.
    if (value.All(char.IsDigit))
    {
      throw new InvalidOperationException(
          $"{key} must carry units (e.g. '00:00:02'); the bare integer '{value}' would bind as days.");
    }

    bool parsed = TimeSpan.TryParseExact(value, ["c", "g"], CultureInfo.InvariantCulture,
        TimeSpanStyles.None, out TimeSpan duration);
    return parsed && duration > TimeSpan.Zero
        ? duration
        : throw new InvalidOperationException(
            $"{key} must be a positive duration in constant format (e.g. '00:00:02'), got '{value}'.");
  }

  private static int? ParseWatchdogAttempts(string? value)
  {
    return value switch
    {
      null => null,
      not null when value.Length > 0 && int.TryParse(value, NumberStyles.Integer,
          CultureInfo.InvariantCulture, out int attempts) && attempts >= 0 => attempts,
      not null when value.Length == 0 => throw new InvalidOperationException(
          "SubAgent:Watchdog:MaxWrapUpAttempts is present but empty. Remove the key or supply a non-negative integer."),
      _ => throw new InvalidOperationException(
          $"SubAgent:Watchdog:MaxWrapUpAttempts must be a non-negative integer, got '{value}'."),
    };
  }

  /// <summary>"SubAgent:DefaultModel" — optional; absent is legal (spawns must then
  ///     pass a model explicitly); present-but-empty is a startup validation error.</summary>
  private static void ValidateDefaultModel(string? defaultModel)
  {
    if (defaultModel is not null && string.IsNullOrWhiteSpace(defaultModel))
    {
      throw new InvalidOperationException(
          "SubAgent:DefaultModel is present but empty. Remove the key or supply a model reference.");
    }
  }

  /// <summary>"SubAgent:MaxConcurrentAgents" — required positive integer; absent,
  ///     non-integer, or below-1 values are startup validation errors.</summary>
  private static int ParseMaxConcurrentAgents(string? maxConcurrentAgents)
  {
    return maxConcurrentAgents switch
    {
      null => throw new InvalidOperationException(
          "SubAgent:MaxConcurrentAgents is required. Set it to a positive integer."),
      not null when !int.TryParse(maxConcurrentAgents, NumberStyles.Integer,
          CultureInfo.InvariantCulture, out int maxConcurrent) || maxConcurrent < 1 =>
        throw new InvalidOperationException(
            $"SubAgent:MaxConcurrentAgents must be a positive integer of at least 1, got '{maxConcurrentAgents}'."),
      _ => int.Parse(maxConcurrentAgents, NumberStyles.Integer, CultureInfo.InvariantCulture),
    };
  }
}
