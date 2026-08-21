using eThangAgent.ConversationDomain;

namespace eThangAgent.Conversation.Domain.Tests;

public class RoleTests
{
    [Fact]
    public void Role_IncludesSystem()
    {
        Assert.Contains(Role.System, Enum.GetValues<Role>());
    }
}
