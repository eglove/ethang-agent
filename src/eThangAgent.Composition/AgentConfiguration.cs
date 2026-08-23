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
        var baseUrl = BindBaseUrl(baseUrlEnv);

        var subAgents = SubAgentConfiguration.Bind(
            configuration["SubAgent:DefaultModel"],
            configuration["SubAgent:ChildTimeoutSeconds"],
            configuration["SubAgent:MaxConcurrentAgents"]);
        var maxToolIterations = MaxToolIterationsConfiguration.Bind(
            configuration["Agent:MaxToolIterations"]);

        return new AgentSettings(apiKey, baseUrl, subAgents, maxToolIterations);
    }

    private static Uri BindBaseUrl(string? baseUrlEnv)
    {
        if (string.IsNullOrWhiteSpace(baseUrlEnv))
            return new Uri("https://openrouter.ai");

        try { return new Uri(baseUrlEnv); }
        catch (UriFormatException)
        {
            throw new InvalidOperationException(
                $"OPENROUTER_BASE_URL must be a valid absolute URI, got '{baseUrlEnv}'.");
        }
    }
}
