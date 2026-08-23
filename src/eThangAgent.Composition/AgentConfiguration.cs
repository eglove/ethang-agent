using Microsoft.Extensions.Configuration;

namespace eThangAgent.Composition;

/// <summary>Shared, strict configuration load for every host: optional appsettings.json next
///     to the executable, overridden by environment variables. Optional-value binding errors
///     throw InvalidOperationException — never coerced, defaulted, or clamped.</summary>
public static class AgentConfiguration
{
    public static AgentSettings Load()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        var baseUrlEnv = Environment.GetEnvironmentVariable("OPENROUTER_BASE_URL");
        var baseUrl = string.IsNullOrWhiteSpace(baseUrlEnv)
            ? new Uri("https://openrouter.ai")
            : new Uri(baseUrlEnv);

        var subAgents = SubAgentConfiguration.Bind(
            configuration["SubAgent:DefaultModel"],
            configuration["SubAgent:ChildTimeoutSeconds"],
            configuration["SubAgent:MaxConcurrentAgents"]);
        var maxToolIterations = MaxToolIterationsConfiguration.Bind(
            configuration["Agent:MaxToolIterations"]);

        return new AgentSettings(apiKey, baseUrl, subAgents, maxToolIterations);
    }
}
