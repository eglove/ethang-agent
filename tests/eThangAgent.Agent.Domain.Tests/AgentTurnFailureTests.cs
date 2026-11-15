using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Provider failures are part of the transcript: the loop appends a
///     "[turn failed]" System line so the persisted conversation records WHY a turn
///     stopped (previously the failure was a transient UI notice only).</summary>
public class AgentTurnFailureTests
{
  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", 100, 0.5f, 8192).Value!;

  [Fact]
  public async Task ProviderFailure_AppendsTurnFailedSystemLine()
  {
    ScriptedModelProvider provider = new(
        Result.Failure<ModelResponse>(new DomainError("ProviderTimeout", "Request timed out.")));
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]),
        new AgentOptions { TurnRetryDelay = (_, _) => Task.CompletedTask });

    Result<string> result = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Message failure = Assert.Single(agent.Conversation.Messages, m => m.Role is Role.System);
    Assert.StartsWith(Agent.TurnFailedPrefix, failure.Content, StringComparison.Ordinal);
    Assert.Contains("Error [ProviderTimeout]: Request timed out.", failure.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ProviderFailureAfterToolCalls_FailureLineFollowsTheToolResult()
  {
    ScriptedModelProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("c1", "t", "{}")])),
        Result.Failure<ModelResponse>(new DomainError("ProviderTimeout", "Request timed out.")));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new OkTool("t")]),
        new AgentOptions { TurnRetryDelay = (_, _) => Task.CompletedTask });

    Result<string> result = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    IReadOnlyList<Message> messages = agent.Conversation.Messages;
    Assert.Equal(Role.Tool, messages[^2].Role);
    Assert.Equal(Role.System, messages[^1].Role);
    Assert.StartsWith(Agent.TurnFailedPrefix, messages[^1].Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SuccessfulTurn_NoFailureLine()
  {
    ScriptedModelProvider provider = new(Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]),
        new AgentOptions { TurnRetryDelay = (_, _) => Task.CompletedTask });

    Result<string> result = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.DoesNotContain(agent.Conversation.Messages, m => m.Content.Contains(Agent.TurnFailedPrefix, StringComparison.Ordinal));
  }

  [Fact]
  public async Task CancelledTurn_NoFailureLine()
  {
    // Cancellation repairs dangling tool calls with InterruptedToolResult; it does not
    // append a turn-failure line (the interruption already explains itself).
    ScriptedModelProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("c1", "t", "{}")])));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new OkTool("t")]),
        new AgentOptions { TurnRetryDelay = (_, _) => Task.CompletedTask });
    using CancellationTokenSource cts = new();
    await cts.CancelAsync().ConfigureAwait(true);

    Result<string> result = await agent.SendMessage("hi", ct: cts.Token);

    Assert.False(result.IsSuccess);
    Assert.Equal(Agent.TurnCancelledCode, result.Error.Code);
    Assert.DoesNotContain(agent.Conversation.Messages, m => m.Content.Contains(Agent.TurnFailedPrefix, StringComparison.Ordinal));
  }

  [Fact]
  public async Task FailureLine_SurfacedThroughOnSystemMessage()
  {
    ScriptedModelProvider provider = new(
        Result.Failure<ModelResponse>(new DomainError("ProviderTimeout", "Request timed out.")));
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]),
        new AgentOptions { TurnRetryDelay = (_, _) => Task.CompletedTask });
    List<string> surfaced = [];
    TurnCallbacks callbacks = new(OnSystemMessage: line => surfaced.Add(line));

    _ = await agent.SendMessage("hi", callbacks: callbacks, ct: TestContext.Current.CancellationToken);

    string? line = Assert.Single(surfaced);
    Assert.StartsWith(Agent.TurnFailedPrefix, line, StringComparison.Ordinal);
  }

  /// <summary>Replays the scripted responses in order and then repeats the LAST one, so a fade
  ///     that the turn retries keeps failing every attempt. Written this way rather than as a
  ///     queue-with-successful-tail on purpose: the turn's whole subject is which failure it
  ///     cannot recover from.</summary>
  private sealed class ScriptedModelProvider(params Result<ModelResponse>[] responses) : IModelProvider
  {
    private readonly Result<ModelResponse>[] _responses = responses;
    private int _next;

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
    {
      int index = Math.Min(_next++, _responses.Length - 1);
      return Task.FromResult(_responses[index]);
    }
  }

  private sealed class OkTool(string name) : ITool
  {
    public ToolDefinition Definition { get; } = new ToolDefinition(name, "desc", []);

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
        => Task.FromResult(new ToolResult("ok", false));
  }
}
