using eThangAgent.Agent.Application;

namespace eThangAgent.Composition;

/// <summary>The SubAgent:Watchdog configuration surface (W1.2): the host-watchdog knobs
///     an operator may tune without recompiling, carried inside <see cref="AgentSettings"/>
///     to the child host through the settings JSON. Null members mean "leave the
///     <see cref="WatchdogOptions"/> default" — the bind has already rejected invalid
///     text, and <see cref="ToOptions"/> is the strict translation into the options the
///     host watchdog consumes. This shape is STJ-friendly by construction (every property
///     type matches its constructor parameter) because <see cref="WatchdogOptions"/>
///     itself is not: its nullable parameters back non-nullable properties, so the
///     settings JSON must speak this type, never the options type.</summary>
public sealed record WatchdogSettings(
    TimeSpan? TickInterval = null,
    TimeSpan? IdleThreshold = null,
    int? MaxWrapUpAttempts = null)
{
  /// <summary>Translates the configured knobs into watchdog options. Members left null
  ///     fall through to WatchdogOptions' own defaults — absent configuration is
  ///     today's behavior, never a substitute value invented here.</summary>
  public WatchdogOptions ToOptions() => MaxWrapUpAttempts is { } attempts
      ? new WatchdogOptions(TickInterval: TickInterval, IdleThreshold: IdleThreshold, MaxWrapUpAttempts: attempts)
      : new WatchdogOptions(TickInterval: TickInterval, IdleThreshold: IdleThreshold);
}
