namespace eThangAgent.ModelDomain;

/// <summary>Provider-neutral reason a model response ended. Translated by each
///     provider ACL from its native vocabulary (OpenRouter's finish_reason).
///     Length means the output-token budget was exhausted mid-response.</summary>
public enum FinishReason
{
  Stop,
  Length,
  ToolCalls,
  ContentFilter,
  Unknown,
}
