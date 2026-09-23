namespace eThangAgent.Provider.Wire;

/// <summary>How message parts ride the OpenAI-compatible request wire. The default
///     keeps parts on the message that carries them; providers whose tool results
///     cannot accept images project them into a synthetic user message instead.</summary>
public enum PartsProjection
{
  /// <summary>Parts serialize on the carrying message itself (user- and tool-role alike).</summary>
  None,

  /// <summary>Tool-role content always serializes flat text; every image part across a
  ///     turn's tool results merges into ONE synthetic user message placed after the
  ///     LAST tool result of the assistant tool_calls block (never between tool
  ///     results), each image preceded by "[computer screenshot for tool call
  ///     &lt;toolCallId&gt;]" — the z.ai/Local projection (spec 5.3).</summary>
  PostTurnUser,
}
