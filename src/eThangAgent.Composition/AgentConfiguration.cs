using eThangAgent.AgentDomain;
using eThangAgent.Zai.ACL;
using Microsoft.Extensions.Configuration;

namespace eThangAgent.Composition;

/// <summary>Shared, strict configuration load for every host: optional appsettings.json next
///     to the executable, overridden by environment variables. Optional-value binding errors
///     throw InvalidOperationException — never coerced, defaulted, or clamped.</summary>
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
        configuration["SubAgent:ChildTimeoutSeconds"],
        configuration["SubAgent:MaxConcurrentAgents"]);

    string? modelId = configuration["Model:Id"];
    return modelId is not null && string.IsNullOrWhiteSpace(modelId)
      ? throw new InvalidOperationException("Model:Id is present but empty. Remove the key or supply a model reference.")
      : new AgentSettings(
        new OpenRouterSettings(
            Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"),
            BindBaseUrl("OPENROUTER_BASE_URL",
                Environment.GetEnvironmentVariable("OPENROUTER_BASE_URL"), "https://openrouter.ai")),
        new ZaiSettings(
            Environment.GetEnvironmentVariable("ZAI_API_KEY"),
            BindBaseUrl("ZAI_BASE_URL",
                Environment.GetEnvironmentVariable("ZAI_BASE_URL"), ZaiConfiguration.DefaultBaseUrl)),
        subAgents,
        modelId);
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
