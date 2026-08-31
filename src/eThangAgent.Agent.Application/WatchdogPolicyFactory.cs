using eThangAgent.AgentDomain;

namespace eThangAgent.Agent.Application;

/// <summary>Builds the domain policy from the host's watchdog options. Lives in the
///     application layer: the domain must not know application configuration types.</summary>
public static class WatchdogPolicyFactory
{
  public static WatchdogPolicy FromOptions(WatchdogOptions options)
  {
    ArgumentNullException.ThrowIfNull(options);
    return new WatchdogPolicy(options.IdleThreshold, options.SettleWait, options.MaxWrapUpAttempts);
  }
}
