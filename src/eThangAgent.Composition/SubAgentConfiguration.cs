using System.Globalization;
using eThangAgent.AgentDomain;

namespace eThangAgent.Composition;

/// <summary>Strict composition-root binding of SubAgent configuration into
///     <see cref="SubAgentOptions"/>. Keys:
///     "SubAgent:DefaultModel" — optional; absent is legal (spawns must then pass a
///     model explicitly); present-but-empty is a startup validation error.
///     "SubAgent:MaxConcurrentAgents" — required positive integer; absent,
///     non-integer, or below-1 values are startup validation errors.
///     "SubAgent:RemoteHost" — optional; "true" opts the session into the
///     out-of-process child host (R3), anything else non-empty is a startup
///     validation error; default is in-process.
///     "SubAgent:Watchdog:TickInterval" / ":IdleThreshold" — optional constant-format
///     (":"-separated) durations; absent keeps the default, empty, non-parseable,
///     non-positive, or bare-integer (a silent days value) text is a startup error.
///     "SubAgent:Watchdog:MaxWrapUpAttempts" — optional non-negative integer; same rule.
///     Nothing is silently coerced or clamped. There is deliberately no child-timeout
///     key (FR-L4/A4): wall-clock is never a child cancellation source.</summary>
public static class SubAgentConfiguration
{
  public static SubAgentOptions Bind(string? defaultModel, string? maxConcurrentAgents,
      out bool remoteHost, string? remoteHostValue = null)
  {
    ValidateDefaultModel(defaultModel);
    int maxConcurrent = ParseMaxConcurrentAgents(maxConcurrentAgents);
    remoteHost = ParseRemoteHost(remoteHostValue); // hosts wire the remote runtime when true
    return new SubAgentOptions(defaultModel, MaxConcurrentAgents: maxConcurrent);
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

  /// <summary>"SubAgent:RemoteHost" — optional; only "true"/"false" (any case)
  ///     bind; anything else is a startup error. The flag is consumed by hosts that
  ///     wire the remote runtime; SubAgentOptions stays provider-neutral.</summary>
  private static bool ParseRemoteHost(string? remoteHost)
  {
    return remoteHost switch
    {
      null => false,
      not null when bool.TryParse(remoteHost, out bool parsed) => parsed,
      _ => throw new InvalidOperationException(
          $"SubAgent:RemoteHost must be 'true' or 'false', got '{remoteHost}'."),
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
