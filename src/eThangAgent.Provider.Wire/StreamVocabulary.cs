using eThangAgent.ModelDomain;

namespace eThangAgent.Provider.Wire;

/// <summary>Vocabulary a provider supplies so its OpenAI-compatible stream can be read
///     by <see cref="OpenAiCompatStreamCore"/>: how the provider's finish_reason strings
///     map onto the provider-neutral enum, and which delta fields carry reasoning text
///     (OpenRouter also accepts a plain <c>reasoning</c> fallback field that z.ai does
///     not document).</summary>
public sealed record StreamVocabulary(
    IReadOnlyDictionary<string, FinishReason> FinishReasons,
    IReadOnlyList<string> ReasoningFields)
{
  /// <summary>The OpenAI-standard vocabulary shared by OpenAI-compatible providers:
  ///     stop / length / tool_calls / content_filter; anything else maps to Unknown.</summary>
  public static readonly IReadOnlyDictionary<string, FinishReason> StandardFinishReasons =
      new Dictionary<string, FinishReason>
      {
        ["stop"] = FinishReason.Stop,
        ["length"] = FinishReason.Length,
        ["tool_calls"] = FinishReason.ToolCalls,
        ["content_filter"] = FinishReason.ContentFilter,
      };
}
