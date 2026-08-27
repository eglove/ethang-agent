namespace eThangAgent.Zai.ACL;

/// <summary>Maps the provider-neutral <see cref="ModelDomain.ReasoningEffort"/>
///     onto z.ai's <c>reasoning_effort</c> wire vocabulary.</summary>
public static class ZaiReasoningEffort
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
