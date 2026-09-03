using eThangAgent.Provider.Wire;

namespace eThangAgent.Local.ACL;

/// <summary>Local servers' streaming vocabulary: the OpenAI-standard finish_reason set
///     (llama.cpp / LM Studio / Ollama's OpenAI-compatible layers emit stop, length,
///     tool_calls, content_filter) and reasoning where the OpenAI-compatible family
///     documents it — the <c>reasoning_content</c> delta field.</summary>
internal static class LocalStreamVocabulary
{
  public static StreamVocabulary Instance { get; } = new(
      StreamVocabulary.StandardFinishReasons,
      ["reasoning_content"]);
}
