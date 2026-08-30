using eThangAgent.ModelDomain;
using eThangAgent.Provider.Wire;

namespace eThangAgent.Zai.ACL;

/// <summary>z.ai's streaming vocabulary: GLM's finish_reason strings add <c>sensitive</c>
///     (content filter) and <c>model_context_window_exceeded</c> (closest actionable
///     meaning: Length) to the OpenAI-standard set, and reasoning streams through the
///     documented <c>reasoning_content</c> delta field.</summary>
internal static class ZaiStreamVocabulary
{
  public static StreamVocabulary Instance { get; } = new(
      new Dictionary<string, FinishReason>(StreamVocabulary.StandardFinishReasons)
      {
        ["sensitive"] = FinishReason.ContentFilter,
        ["model_context_window_exceeded"] = FinishReason.Length,
      },
      ["reasoning_content"]);
}
