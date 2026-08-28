namespace eThangAgent.ModelDomain;

/// <summary>Reasoning-effort levels the user picks in the host's effort picker (max,
///     xhigh, high, medium, low, minimal, none). Null (unset) means the provider's own
///     default applies. Both provider ACLs translate it onto their wire format: z.ai's
///     <c>reasoning_effort</c> and OpenRouter's unified <c>reasoning.effort</c> (which
///     accepts exactly this vocabulary and normalizes to the nearest level each model
///     supports).</summary>
public enum ReasoningEffort
{
  Max,
  ExtraHigh,
  High,
  Medium,
  Low,
  Minimal,
  None,
}
