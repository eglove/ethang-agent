using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Ag = eThangAgent.AgentDomain.Agent;

namespace eThangAgent.Agent.Application.Tests;

public class SendMessageCommandHandlerTests
{
  [Fact]
  public async Task Handle_DelegatesToAgentAndReturnsResult()
  {
    StubModelProvider provider = new(
        Result.Success(new ModelResponse("response", [])));
    Ag agent = new(provider, new Conversation(),
        ModelConfig.Create("m", null, 100, 0.5f).Value!, new ToolRegistry([]));
    SendMessageCommandHandler handler = new(agent);

    Result<string> result = await handler.Handle(new SendMessageCommand("hello"), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("response", result.Value);
  }

  [Fact]
  public async Task Handle_PropagatesFailure()
  {
    DomainError error = new("FAIL", "bad");
    StubModelProvider provider = new(Result.Failure<ModelResponse>(error));
    Ag agent = new(provider, new Conversation(),
        ModelConfig.Create("m", null, 100, 0.5f).Value!, new ToolRegistry([]));
    SendMessageCommandHandler handler = new(agent);

    Result<string> result = await handler.Handle(new SendMessageCommand("hello"), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal(error, result.Error);
  }

  private sealed class StubModelProvider(Result<ModelResponse> result) : IModelProvider
  {
    private readonly Result<ModelResponse> _result = result;

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
        => Task.FromResult(_result);
  }
}
