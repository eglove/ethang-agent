namespace eThangAgent.ConversationDomain;

public interface IConversationRepository
{
  Conversation GetCurrent();
  void Save(Conversation conversation);
}
