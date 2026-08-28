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
    ValidateDefaultModel(defaultModel);
    int maxConcurrent = ParseMaxConcurrentAgents(maxConcurrentAgents);
    int? childTimeout = ParseChildTimeout(childTimeoutSeconds);

    SubAgentOptions bound = childTimeout is { } seconds
        ? new SubAgentOptions(defaultModel, TimeSpan.FromSeconds(seconds), maxConcurrent)
        : new SubAgentOptions(defaultModel, MaxConcurrentAgents: maxConcurrent);
    return bound;
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
    if (maxConcurrentAgents is null)
    {
      throw new InvalidOperationException(
          "SubAgent:MaxConcurrentAgents is required. Set it to a positive integer.");
    }

    if (!int.TryParse(maxConcurrentAgents, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int maxConcurrent))
    {
      throw new InvalidOperationException(
          $"SubAgent:MaxConcurrentAgents must be a positive integer, got '{maxConcurrentAgents}'.");
    }

    if (maxConcurrent < 1)
    {
      throw new InvalidOperationException(
          $"SubAgent:MaxConcurrentAgents must be at least 1, got '{maxConcurrentAgents}'.");
    }

    int bound = maxConcurrent;
    return bound;
  }

  /// <summary>"SubAgent:ChildTimeoutSeconds" — defaults to absent (null) when the key
  ///     is missing; zero, negative, or non-integer values are startup validation errors.</summary>
  private static int? ParseChildTimeout(string? childTimeoutSeconds)
  {
    if (childTimeoutSeconds is null)
    {
      return null;
    }

    if (!int.TryParse(childTimeoutSeconds, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int seconds))
    {
      throw new InvalidOperationException(
          $"SubAgent:ChildTimeoutSeconds must be an integer number of seconds, got '{childTimeoutSeconds}'.");
    }

    if (seconds <= 0)
    {
      throw new InvalidOperationException(
          $"SubAgent:ChildTimeoutSeconds must be positive, got '{seconds}'.");
    }

    int timeoutSeconds = seconds;
    return timeoutSeconds;
  }
}
