namespace eThangAgent.Conversation.Domain;

public interface IConversationRepository
{
    Conversation GetCurrent();
    void Save(Conversation conversation);
}
