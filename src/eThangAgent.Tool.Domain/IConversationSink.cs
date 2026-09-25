namespace eThangAgent.ToolDomain;

/// <summary>Port the Tool Domain owns for appending an out-of-turn System
///     message to the session conversation (spec #28 task 2, the
///     SkillDirectoryReloader precedent). The composition root adapts the
///     Conversation Domain's Conversation to it — the domain depends only on
///     this contract, never on another context's types (Tool Domain cannot
///     reference Conversation Domain: Conversation Domain references Tool
///     Domain). Delivery is best-effort by contract: implementers swallow
///     failures so a broken transcript never fails the tool.</summary>
public interface IConversationSink
{
  /// <summary>Appends one System message, out of turn.</summary>
  void AddSystemMessage(string text);
}
