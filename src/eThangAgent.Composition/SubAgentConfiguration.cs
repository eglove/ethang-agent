using System.Globalization;
using eThangAgent.AgentDomain;

namespace eThangAgent.Composition;

/// <summary>Strict composition-root binding of SubAgent configuration into
///     <see cref="SubAgentOptions"/>. Keys:
///     "SubAgent:DefaultModel" — optional; absent is legal (spawns must then pass a
///     model explicitly); present-but-empty is a startup validation error.
///     "SubAgent:MaxConcurrentAgents" — required positive integer; absent,
///     non-integer, or below-1 values are startup validation errors.
///     "SubAgent:RemoteHost" — optional; "true" opts the session into the
///     out-of-process child host (R3), anything else non-empty is a startup
///     validation error; default is in-process. Nothing is
///     silently coerced or clamped. There is deliberately no timeout key (FR-L4/A4):
///     wall-clock is never a child cancellation source.</summary>
public static class SubAgentConfiguration
{
  public static SubAgentOptions Bind(string? defaultModel, string? maxConcurrentAgents,
      out bool remoteHost, string? remoteHostValue = null)
  {
    ValidateDefaultModel(defaultModel);
    int maxConcurrent = ParseMaxConcurrentAgents(maxConcurrentAgents);
    remoteHost = ParseRemoteHost(remoteHostValue); // hosts wire the remote runtime when true
    return new SubAgentOptions(defaultModel, MaxConcurrentAgents: maxConcurrent);
  }

  /// <summary>"SubAgent:RemoteHost" — optional; only "true"/"false" (any case)
  ///     bind; anything else is a startup error. The flag is consumed by hosts that
  ///     wire the remote runtime; SubAgentOptions stays provider-neutral.</summary>
  private static bool ParseRemoteHost(string? remoteHost)
  {
    return remoteHost switch
    {
      null => false,
      not null when bool.TryParse(remoteHost, out bool parsed) => parsed,
      _ => throw new InvalidOperationException(
          $"SubAgent:RemoteHost must be 'true' or 'false', got '{remoteHost}'."),
    };
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
