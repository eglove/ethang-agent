using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Port the Tool Domain owns for reading and shrinking a conversation's
///     context. The composition root adapts the Conversation Domain's
///     ConversationContextService to it — the domain depends only on this contract,
///     never on another context's types, so any implementation that can express
///     list/remove/shorten can be wired in without domain changes.</summary>
public interface IConversationContextService
{
  /// <summary>Renders the current message list with stable 1-based indexes.</summary>
  string List();

  /// <summary>Removes the selected messages; success returns the shrink sentinel line.</summary>
  Result<string> Remove(string selection);

  /// <summary>Replaces one selected message's content; success returns the shrink sentinel line.</summary>
  Result<string> Shorten(string selection, string text);
}
