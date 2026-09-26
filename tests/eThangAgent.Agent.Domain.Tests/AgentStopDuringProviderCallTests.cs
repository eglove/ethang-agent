using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>A user stop that lands DURING the provider call must surface as TurnCancelled,
///     never as a "[turn failed] ProviderTimeout" line: the provider ACL maps every
///     OperationCanceledException to ProviderTimeout (it cannot tell a caller stop from
///     a genuine HttpClient timeout), so the loop's own ct is the only authority for
///     who cancelled.</summary>
public class AgentStopDuringProviderCallTests
{
  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", null, 100, 0.5f, 8192).Value!;

  [Fact]
  public async Task CancelledDuringProviderCall_TurnCancelled_NotTurnFailed()
  {
    using CancellationTokenSource cts = new();
    ScriptedProvider provider = new(() => cts.CancelAsync());
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]));

    Result<string> result = await agent.SendMessage("hi", ct: cts.Token);

    Assert.False(result.IsSuccess);
    Assert.Equal(Agent.TurnCancelledCode, result.Error.Code);
    Assert.DoesNotContain(agent.Conversation.Messages,
        m => m.Content.Contains(Agent.TurnFailedPrefix, StringComparison.Ordinal));
  }

  [Fact]
  public async Task ProviderTimeoutWithoutCancellation_StillTurnFailed()
  {
    // The genuine-timeout path is unchanged: no ct fires, the failure line stays.
    ScriptedProvider provider = new(() => { },
        first: Result.Failure<ModelResponse>(new DomainError("ProviderTimeout", "Request timed out.")));
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]));

    Result<string> result = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Message failure = Assert.Single(agent.Conversation.Messages, m => m.Role is Role.System);
    Assert.StartsWith(Agent.TurnFailedPrefix, failure.Content, StringComparison.Ordinal);
    Assert.Contains("ProviderTimeout", failure.Content, StringComparison.Ordinal);
  }

  private sealed class ScriptedProvider(Action onCancel,
      Result<ModelResponse>? first = null) : IModelProvider
  {
    private bool _invoked;

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
    {
      Result<ModelResponse> response = first is { } f && !_invoked ? f
          : Result.Failure<ModelResponse>(new DomainError("ProviderTimeout", "Request timed out."));
      if (!_invoked)
      {
        _invoked = true;
        onCancel();
      }

      return Task.FromResult(response);
    }
  }
}
