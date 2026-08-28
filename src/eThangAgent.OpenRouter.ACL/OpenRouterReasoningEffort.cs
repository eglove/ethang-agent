namespace eThangAgent.OpenRouter.ACL;

/// <summary>Maps the provider-neutral <see cref="ModelDomain.ReasoningEffort"/>
///     onto OpenRouter's unified <c>reasoning.effort</c> wire vocabulary. OpenRouter
///     documents exactly these seven values and normalizes them to the nearest level
///     each model supports, so the mapping is a pure identity.</summary>
public static class OpenRouterReasoningEffort
{
  public static string ToWire(ModelDomain.ReasoningEffort effort) => effort switch
  {
    ModelDomain.ReasoningEffort.Max => "max",
    ModelDomain.ReasoningEffort.ExtraHigh => "xhigh",
    ModelDomain.ReasoningEffort.High => "high",
    ModelDomain.ReasoningEffort.Medium => "medium",
    ModelDomain.ReasoningEffort.Low => "low",
    ModelDomain.ReasoningEffort.Minimal => "minimal",
    ModelDomain.ReasoningEffort.None => "none",
    _ => throw new ArgumentOutOfRangeException(nameof(effort), effort, "Unknown reasoning effort.")
  };
}
