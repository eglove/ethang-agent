using System.Globalization;
using eThangAgent.AgentDomain;

namespace eThangAgent.Composition;

/// <summary>Strict composition-root binding of SubAgent configuration into
///     <see cref="SubAgentOptions"/>. Keys:
///     "SubAgent:DefaultModel" — optional; absent is legal (spawns must then pass a
///     model explicitly); present-but-empty is a startup validation error.
///     "SubAgent:MaxConcurrentAgents" — required positive integer; absent,
///     non-integer, or below-1 values are startup validation errors. Nothing is
///     silently coerced or clamped. There is deliberately no timeout key (FR-L4/A4):
///     wall-clock is never a child cancellation source.</summary>
public static class SubAgentConfiguration
{
  public static SubAgentOptions Bind(string? defaultModel, string? maxConcurrentAgents)
  {
    ValidateDefaultModel(defaultModel);
    int maxConcurrent = ParseMaxConcurrentAgents(maxConcurrentAgents);
    return new SubAgentOptions(defaultModel, MaxConcurrentAgents: maxConcurrent);
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
