using eThangAgent.AgentDomain;
using eThangAgent.Zai.ACL;
using Microsoft.Extensions.Configuration;

namespace eThangAgent.Composition;

/// <summary>Shared, strict configuration load for every host: optional appsettings.json next
///     to the executable, overridden by environment variables. Optional-value binding errors
///     throw InvalidOperationException — never coerced, defaulted, or clamped. Provider
///     API keys are NOT loaded here: each host sources them from its own credential store
///     (the Desktop reads DPAPI-protected keys from app preferences) and overlays them via
///     <see cref="AgentSettings.WithApiKeys"/>.</summary>
public static class AgentConfiguration
{
  public static AgentSettings Load()
  {
    IConfigurationRoot configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
        .AddEnvironmentVariables()
        .Build();

    SubAgentOptions subAgents = SubAgentConfiguration.Bind(
        configuration["SubAgent:DefaultModel"],
        configuration["SubAgent:MaxConcurrentAgents"],
        out bool remoteHost,
        configuration["SubAgent:RemoteHost"]);
    WatchdogSettings? watchdog = SubAgentConfiguration.BindWatchdog(
        configuration["SubAgent:Watchdog:TickInterval"],
        configuration["SubAgent:Watchdog:IdleThreshold"],
        configuration["SubAgent:Watchdog:MaxWrapUpAttempts"]);

    return new AgentSettings(
        new OpenRouterSettings(
            null,
#pragma warning disable S1075 // Anchored provider default; per-host config (the env variable) overrides it.
            BindBaseUrl("OPENROUTER_BASE_URL",
                Environment.GetEnvironmentVariable("OPENROUTER_BASE_URL"), "https://openrouter.ai")),
#pragma warning restore S1075
        new ZaiSettings(
            null,
            BindBaseUrl("ZAI_BASE_URL",
                Environment.GetEnvironmentVariable("ZAI_BASE_URL"), ZaiConfiguration.DefaultBaseUrl),
            BindEndpointMode(Environment.GetEnvironmentVariable("ZAI_ENDPOINT_MODE"))),
        subAgents,
        RemoteHost: remoteHost,
        Watchdog: watchdog);
  }

  /// <summary>Parses the endpoint-mode variable: exactly <c>coding</c> or <c>general</c>.
  ///     Absent or empty → CodingPlan, the GLM Coding Plan default. Any other value throws —
  ///     configuration is never coerced.</summary>
  private static ZaiEndpointMode BindEndpointMode(string? variableValue)
  {
    return variableValue switch
    {
      null or "" => ZaiEndpointMode.CodingPlan,
      _ => variableValue.TryParseConfigValue(out ZaiEndpointMode mode)
          ? mode
          : throw new InvalidOperationException(
              "ZAI_ENDPOINT_MODE must be 'coding' or 'general', got '" + variableValue + "'."),
    };
  }

  private static Uri BindBaseUrl(string variableName, string? baseUrlEnv, string defaultUrl)
  {
    if (string.IsNullOrWhiteSpace(baseUrlEnv))
    {
      return new Uri(defaultUrl);
    }

    try
    {
      return new Uri(baseUrlEnv);
    }
    catch (UriFormatException)
    {
      throw new InvalidOperationException(
          $"{variableName} must be a valid absolute URI, got '{baseUrlEnv}'.");
    }
  }
}
