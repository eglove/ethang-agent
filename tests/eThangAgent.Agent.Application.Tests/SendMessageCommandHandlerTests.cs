using Ag = eThangAgent.AgentDomain.Agent;
using eThangAgent.ModelDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

public class SendMessageCommandHandlerTests
{
    [Fact]
    public async Task Handle_DelegatesToAgentAndReturnsResult()
    {
        var provider = new StubModelProvider(Result<string>.Success("response"));
        var agent = new Ag(provider, new Conversation(),
            ModelConfig.Create("m", 100, 0.5f).Value!);
        var handler = new SendMessageCommandHandler(agent);

        var result = await handler.Handle(new SendMessageCommand("hello"));

        Assert.True(result.IsSuccess);
        Assert.Equal("response", result.Value);
    }

    [Fact]
    public async Task Handle_PropagatesFailure()
    {
        var error = new Error("FAIL", "bad");
        var provider = new StubModelProvider(Result<string>.Failure(error));
        var agent = new Ag(provider, new Conversation(),
            ModelConfig.Create("m", 100, 0.5f).Value!);
        var handler = new SendMessageCommandHandler(agent);

        var result = await handler.Handle(new SendMessageCommand("hello"));

        Assert.False(result.IsSuccess);
        Assert.Equal(error, result.Error);
    }

    private sealed class StubModelProvider : IModelProvider
    {
        private readonly Result<string> _result;
        public StubModelProvider(Result<string> result) => _result = result;

        public Task<Result<string>> SendAsync(ModelConfig config, string prompt, CancellationToken ct)
            => Task.FromResult(_result);
    }
}
