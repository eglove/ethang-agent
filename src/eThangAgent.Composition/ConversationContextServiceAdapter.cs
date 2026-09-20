using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition;

/// <summary>Composition adapter: exposes the session's shared Conversation through
///     the Tool Domain's IConversationContextService port. The Conversation Domain's
///     ConversationContextService performs the surgery; this type only bridges
///     domain boundaries at the composition root.</summary>
public sealed class ConversationContextServiceAdapter(Conversation conversation) : IConversationContextService
{
  private readonly ConversationContextService _service = new(conversation);

  public string List() => _service.List();

  public Result<string> Remove(string selection) => _service.Remove(selection);

  public Result<string> Shorten(string selection, string text) => _service.Shorten(selection, text);
}
