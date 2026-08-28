using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.Streaming;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>Shared fixtures for desktop UI tests: a stub turn runner, a stub store,
///     and the real RootSessionLifecycle so VM behavior is exercised against real
///     persistence semantics.</summary>
internal static class TestFixtures
{
  /// <summary>Runtime stub for view-model tests: records interrupt calls, never starts runs.</summary>
  internal sealed class StubAgentRuntime : IAgentRuntime
  {
    public int InterruptAllCount { get; private set; }

    public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
    {
      ArgumentNullException.ThrowIfNull(record);
      return Task.FromResult(Result.Success(record.Id));
    }

    public void Interrupt(AgentId? childId = null)
    {
      if (childId is null)
      {
        InterruptAllCount++;
      }
    }
  }

  internal sealed class StubStore : IAgentStore
  {
    public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success("saved"));
    public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success("updated"));
    public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<AgentRecord>(new DomainError("NotFound", "not found")));
    public Task<Result<string>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default)
        => Task.FromResult(Result.Success("appended"));
    public Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<Message>>([]));
    public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([]));
    public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([]));
  }

  /// <summary>Builds an AgentSessionViewModel whose turn runner streams "ack" and succeeds.
  ///     When <paramref name="marshalToUIThread"/> is true the stream sink marshals onto
  ///     the UI thread (production shape — for headless window tests); otherwise events
  ///     apply on the pump thread (deterministic for plain unit tests).</summary>
  internal static AgentSessionViewModel CreateViewModel(bool marshalToUIThread = false)
  {
    static Task<Result<string>> runner(SendMessageCommand command, CancellationToken ct, TurnCallbacks? callbacks, Action<string>? onNotice = null)
    {
      callbacks?.OnContentDelta?.Invoke("ack");
      return Task.FromResult(Result.Success("ack"));
    }

    AgentSessionViewModel? vmRef = null;
    Func<UiStreamEvent, Task> sink = marshalToUIThread
        ? (e => vmRef!.ApplyUiStreamEventOnUIThreadAsync(e))
        : e => vmRef!.ApplyUiStreamEventAsync(e);
    AgentSessionViewModel vm = new(
runner,
        new RootSessionLifecycle(new StubStore()),
        AgentId.NewId(),
        new Conversation(),
        provider: "OpenRouter",
        modelId: "test/model",
        new AgentSessionViewModelOptions { WorkspaceRoot = @"C:\work\demo", UiStreamSink = sink });
    vmRef = vm;
    return vm;
  }
}
