using eThangAgent.ModelDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;
using eThangAgent.AgentDomain;

namespace eThangAgent.AgentDomain.Tests;

public class AgentTests
{
    private static ModelConfig DefaultConfig =>
        ModelConfig.Create("test-model", 100, 0.5f).Value!;

    [Fact]
    public async Task SendMessage_OnSuccess_AddsBothMessages()
    {
        var provider = new FakeModelProvider(Result<string>.Success("Hello back"));
        var conversation = new Conversation();
        var agent = new Agent(provider, conversation, DefaultConfig);

        var result = await agent.SendMessage("Hi");

        Assert.True(result.IsSuccess);
        Assert.Equal("Hello back", result.Value);
        Assert.Equal(2, conversation.Messages.Count);
        Assert.Equal(Role.User, conversation.Messages[0].Role);
        Assert.Equal("Hi", conversation.Messages[0].Content);
        Assert.Equal(Role.Assistant, conversation.Messages[1].Role);
        Assert.Equal("Hello back", conversation.Messages[1].Content);
    }

    [Fact]
    public async Task SendMessage_OnFailure_DoesNotAddAssistantMessage()
    {
        var error = new Error("Test", "fail");
        var provider = new FakeModelProvider(Result<string>.Failure(error));
        var conversation = new Conversation();
        var agent = new Agent(provider, conversation, DefaultConfig);

        var result = await agent.SendMessage("Hi");

        Assert.False(result.IsSuccess);
        Assert.Equal(error, result.Error);
        Assert.Single(conversation.Messages);
        Assert.Equal(Role.User, conversation.Messages[0].Role);
        Assert.Equal("Hi", conversation.Messages[0].Content);
    }

    [Fact]
    public async Task SendMessage_PassesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var provider = new FakeModelProvider(Result<string>.Success("ok"));
        var agent = new Agent(provider, new Conversation(), DefaultConfig);

        var result = await agent.SendMessage("Hi", cts.Token);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Constructor_ExposesConversationAndConfig()
    {
        var provider = new FakeModelProvider(Result<string>.Success("ok"));
        var conversation = new Conversation();
        var config = DefaultConfig;
        var agent = new Agent(provider, conversation, config);

        Assert.Same(conversation, agent.Conversation);
        Assert.Same(config, agent.Config);
    }

    private sealed class FakeModelProvider : IModelProvider
    {
        private readonly Result<string> _result;
        public FakeModelProvider(Result<string> result) => _result = result;

        public Task<Result<string>> SendAsync(ModelConfig config, string prompt, CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
                return Task.FromResult(Result<string>.Failure(new Error("Cancelled", "Cancelled")));
            return Task.FromResult(_result);
        }
    }
}
