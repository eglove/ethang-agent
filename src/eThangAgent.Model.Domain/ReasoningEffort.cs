namespace eThangAgent.ModelDomain;

/// <summary>Reasoning-effort levels for providers that expose one (z.ai's GLM vocabulary:
///     max, xhigh, high, medium, low, minimal, none). Null (unset) means the provider's
///     own default applies. OpenRouter has no session-wide equivalent and ignores it.</summary>
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
