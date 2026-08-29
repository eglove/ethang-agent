namespace eThangAgent.Zai.ACL;

/// <summary>Configuration-string representation of <see cref="ZaiEndpointMode"/> — the one
///     mapping every surface that persists or reads the mode shares
///     (<c>ZAI_ENDPOINT_MODE</c>, the Desktop's <c>zai_endpoint_mode</c> preference).</summary>
public static class ZaiEndpointModeExtensions
{
  /// <summary>The exact token stored for <paramref name="mode"/>.</summary>
  public static string ToConfigValue(this ZaiEndpointMode mode) => mode switch
  {
    ZaiEndpointMode.CodingPlan => "coding",
    ZaiEndpointMode.GeneralApi => "general",
    _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown endpoint mode."),
  };

  /// <summary>Strict parse: only the exact tokens <see cref="ToConfigValue"/> produces.
  ///     No trimming, no case-folding — unknown input is rejected, never coerced.</summary>
  public static bool TryParseConfigValue(this string? value, out ZaiEndpointMode mode)
  {
    switch (value)
    {
      case "coding":
        mode = ZaiEndpointMode.CodingPlan;
        return true;
      case "general":
        mode = ZaiEndpointMode.GeneralApi;
        return true;
      default:
        mode = ZaiEndpointMode.CodingPlan;
        return false;
    }
  }
}
