using eThangAgent.AgentDomain;
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

    string? apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
    string? baseUrlEnv = Environment.GetEnvironmentVariable("OPENROUTER_BASE_URL");
    Uri baseUrl = BindBaseUrl(baseUrlEnv);

    SubAgentOptions subAgents = SubAgentConfiguration.Bind(
        configuration["SubAgent:DefaultModel"],
        configuration["SubAgent:ChildTimeoutSeconds"],
        configuration["SubAgent:MaxConcurrentAgents"]);

    string? modelId = configuration["Model:Id"];
    return modelId is not null && string.IsNullOrWhiteSpace(modelId)
      ? throw new InvalidOperationException("Model:Id is present but empty. Remove the key or supply a model reference.")
      : new AgentSettings(apiKey, baseUrl, subAgents, modelId);
  }

  private static Uri BindBaseUrl(string? baseUrlEnv)
  {
    if (string.IsNullOrWhiteSpace(baseUrlEnv))
    {
      return new Uri("https://openrouter.ai");
    }

    try
    {
      return new Uri(baseUrlEnv);
    }
    catch (UriFormatException)
    {
      throw new InvalidOperationException(
          $"OPENROUTER_BASE_URL must be a valid absolute URI, got '{baseUrlEnv}'.");
    }
  }
}
