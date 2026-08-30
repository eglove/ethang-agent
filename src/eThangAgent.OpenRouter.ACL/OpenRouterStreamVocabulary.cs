using eThangAgent.Provider.Wire;

namespace eThangAgent.OpenRouter.ACL;

/// <summary>OpenRouter's streaming vocabulary: the OpenAI-standard finish_reason set
///     (its reasoning-effort mapping lives in request building, not the stream), and
///     reasoning deltas under <c>reasoning_content</c> with a plain <c>reasoning</c>
///     fallback field.</summary>
internal static class OpenRouterStreamVocabulary
{
  public static StreamVocabulary Instance { get; } = new(
      StreamVocabulary.StandardFinishReasons,
      ["reasoning_content", "reasoning"]);
}
