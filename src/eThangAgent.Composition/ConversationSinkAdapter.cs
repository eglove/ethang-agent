using eThangAgent.ConversationDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition;

/// <summary>Composition adapter: exposes the session's Conversation through the
///     Tool Domain's IConversationSink port (spec #28 task 3). Tool Domain cannot
///     reference Conversation Domain (the reference runs the other way), so the
///     out-of-turn System-message append crosses this seam at the composition
///     root; delivery failures are swallowed — the sink is best-effort by
///     contract (the SkillDirectoryReloader precedent).</summary>
public sealed class ConversationSinkAdapter(Func<Conversation?> conversation) : IConversationSink
{
  // CA1031: the catch-all IS the named best-effort decision (spec #28 task 2) -
  // a broken conversation append may never fail the tool.
#pragma warning disable CA1031 // Do not catch general exception types
  public void AddSystemMessage(string text)
  {
    try
    {
      conversation()?.AddSystemMessage(text);
    }
    catch
    {
      // Best-effort (named decision): the transcript never fails the tool.
    }
  }
#pragma warning restore CA1031 // Do not catch general exception types
}
