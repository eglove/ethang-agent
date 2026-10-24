namespace eThangAgent.OpenRouter.ACL;

/// <summary>The reasoning-summary levels OpenRouter's responses surface accepts on
///     <c>reasoning.summary</c>. The provider writes the summary itself — the harness
///     never summarizes reasoning, it only asks for the provider's own display
///     separation (parent #261 design rule 4). Null (unset) means the key is omitted
///     and the provider default applies, exactly like every other nullable knob.</summary>
public enum ReasoningSummaryLevel
{
  Auto,
  Concise,
  Detailed,
}

/// <summary>Maps <see cref="ReasoningSummaryLevel"/> onto OpenRouter's
///     <c>reasoning.summary</c> wire vocabulary (auto, concise, detailed) — a pure
///     identity, kept as a mapper so the enum's C# names never leak onto the wire.</summary>
public static class OpenRouterReasoningSummary
{
  public static string ToWire(ReasoningSummaryLevel summary) => summary switch
  {
    ReasoningSummaryLevel.Auto => "auto",
    ReasoningSummaryLevel.Concise => "concise",
    ReasoningSummaryLevel.Detailed => "detailed",
    _ => throw new ArgumentOutOfRangeException(nameof(summary), summary, "Unknown reasoning summary level."),
  };
}
