using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

public class AgentIncrementalPersistenceTests
{
  private sealed class ScriptedModelProvider(params Result<ModelResponse>[] responses) : IModelProvider
  {
    private readonly Queue<Result<ModelResponse>> _responses = new(responses);

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
        => Task.FromResult(_responses.Count > 0 ? _responses.Dequeue()
            : Result.Success(new ModelResponse("fin", [])));
  }

  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", null, 100, 0.5f, 8192).Value!;

  private sealed class RecordingSink : IChildTranscriptStore, IAsyncDisposable
  {
    public List<Message> Appended { get; } = [];
    public List<IReadOnlyList<Message>> Replaced { get; } = [];
    public int _flushed;

    public Task AppendAsync(Message message)
    {
      Appended.Add(message);
      return Task.CompletedTask;
    }

    public Task FlushAsync() => Task.CompletedTask;

    public Task<int> FlushedCount() => Task.FromResult(_flushed);

    public Task ReplaceAsync(IReadOnlyList<Message> messages)
    {
      Replaced.Add(messages);
      _flushed = messages.Count;
      return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  [Fact]
  public async Task SendMessage_WithSink_EveryConversationMutationIsPersistedImmediately()
  {
    ScriptedModelProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("call_1", "read", "{}")])),
        Result.Success(new ModelResponse("done", [])));
    FakeTool tool = new("read", "file content");
    RecordingSink wired = new();
    Conversation conversation = new();
    Agent agent = new(provider, conversation, DefaultConfig, new ToolRegistry([tool]),
        new AgentOptions { TranscriptSink = wired });

    Result<string> result = await agent.SendMessage("read file", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    Assert.Equal(4, wired.Appended.Count);
    Assert.Equal(Role.User, wired.Appended[0].Role);
    Assert.Equal(Role.Assistant, wired.Appended[1].Role);
    Assert.Equal(Role.Tool, wired.Appended[2].Role);
    Assert.Equal(Role.Assistant, wired.Appended[3].Role);
  }

  [Fact]
  public async Task SendMessage_WithSink_ProviderFailureStillPersistsPrefix()
  {
    ScriptedModelProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("call_1", "read", "{}")])),
        Result.Failure<ModelResponse>(new DomainError("Boom", "provider died")));
    FakeTool tool = new("read", "file content");
    RecordingSink wired = new();
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([tool]),
        new AgentOptions { TranscriptSink = wired });

    Result<string> result = await agent.SendMessage("read file", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    // user + assistant tool-call + tool result + [turn failed] system line
    Assert.Equal(4, wired.Appended.Count);
    Assert.Contains("[turn failed]", wired.Appended[3].Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SendMessage_WithoutSink_NoSinkNoEvents()
  {
    ScriptedModelProvider provider = new(Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]));

    Result<string> result = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    Assert.Equal(2, agent.Conversation.Messages.Count);
  }
}
