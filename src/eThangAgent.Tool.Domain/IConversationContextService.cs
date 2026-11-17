using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Port the Tool Domain owns for reading and shrinking a conversation's
///     context. The composition root adapts the Conversation Domain's
///     ConversationContextService to it — the domain depends only on this contract,
///     never on another context's types, so any implementation that can express
///     list/remove/shorten/trim can be wired in without domain changes.</summary>
public interface IConversationContextService
{
  /// <summary>Renders the current message list with stable 1-based indexes.</summary>
  string List();

  /// <summary>Renders the trimmable tool results: one line each with the ordinal a trim
  ///     names, the message position Remove and Shorten name, the tool, the size, and
  ///     the identifying arguments. Biggest first.</summary>
  string ListTrimmable();

  /// <summary>Removes the selected messages; success returns the shrink sentinel line.</summary>
  Result<string> Remove(string selection);

  /// <summary>Replaces one selected message's content; success returns the shrink sentinel line.</summary>
  Result<string> Shorten(string selection, string text);

  /// <summary>Replaces the selected tool results' content with relocation markers;
  ///     success returns the shrink sentinel line.</summary>
  Result<string> TrimToolResults(string selection);
}
