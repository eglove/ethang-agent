using eThangAgent.ConversationDomain;

namespace eThangAgent.Composition;

public class InMemoryConversationRepository : IConversationRepository
{
  private Conversation _current = new();

  public Conversation GetCurrent() => _current;
  public void Save(Conversation conversation) => _current = conversation;
}
