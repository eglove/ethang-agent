using System.Globalization;
using eThangAgent.AgentDomain;

namespace eThangAgent.Composition;

/// <summary>Strict composition-root binding of SubAgent configuration into
///     <see cref="SubAgentOptions"/>. Keys:
///     "SubAgent:DefaultModel" — optional; absent is legal (spawns must then pass a
///     model explicitly); present-but-empty is a startup validation error.
///     "SubAgent:ChildTimeoutSeconds" — defaults to 300 when absent; zero, negative,
///     or non-integer values are startup validation errors.
///     "SubAgent:MaxConcurrentAgents" — required positive integer; absent,
///     non-integer, or below-1 values are startup validation errors. Nothing is
///     silently coerced or clamped.</summary>
public static class SubAgentConfiguration
{
  public static SubAgentOptions Bind(string? defaultModel, string? childTimeoutSeconds,
      string? maxConcurrentAgents)
  {
    return defaultModel is not null && string.IsNullOrWhiteSpace(defaultModel)
      ? throw new InvalidOperationException(
          "SubAgent:DefaultModel is present but empty. Remove the key or supply a model reference.")
      : maxConcurrentAgents is null
      ? throw new InvalidOperationException(
          "SubAgent:MaxConcurrentAgents is required. Set it to a positive integer.")
      : !int.TryParse(maxConcurrentAgents, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int maxConcurrent)
      ? throw new InvalidOperationException(
          $"SubAgent:MaxConcurrentAgents must be a positive integer, got '{maxConcurrentAgents}'.")
      : maxConcurrent < 1
      ? throw new InvalidOperationException(
          $"SubAgent:MaxConcurrentAgents must be at least 1, got '{maxConcurrentAgents}'.")
      : childTimeoutSeconds is null
      ? new SubAgentOptions(defaultModel, MaxConcurrentAgents: maxConcurrent)
      : !int.TryParse(childTimeoutSeconds, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int seconds)
      ? throw new InvalidOperationException(
          $"SubAgent:ChildTimeoutSeconds must be an integer number of seconds, got '{childTimeoutSeconds}'.")
      : seconds <= 0
      ? throw new InvalidOperationException(
          $"SubAgent:ChildTimeoutSeconds must be positive, got {seconds}.")
      : new SubAgentOptions(defaultModel, TimeSpan.FromSeconds(seconds), maxConcurrent);
  }
}
