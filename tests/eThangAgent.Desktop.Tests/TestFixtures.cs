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

    public Result<bool> Deliver(AgentId id, PendingMessage message)
        => Result.Success(true);

    public Task<Result<AgentRunOutcome>> WhenSettledAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<AgentRunOutcome>(new DomainError("NotFound", "not found")));

    public void InterruptSubtree(AgentId rootOfSubtree) => InterruptAllCount++;

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
    public Task<Result<string>> ReplaceTranscriptAsync(AgentId id, IReadOnlyList<Message> messages, CancellationToken ct = default)
          => Task.FromResult(Result.Success(id.ToString()));

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
      => CreateViewModel(
          static (command, ct, callbacks, onNotice) =>
          {
            callbacks?.OnContentDelta?.Invoke("ack");
            return Task.FromResult(Result.Success("ack"));
          },
          marshalToUIThread);

  /// <summary>Builds an AgentSessionViewModel around a custom turn runner. The VM wraps
  ///     every runner in DesktopHost.OffUiThread, so the runner body executes on a worker
  ///     thread — the production shape for callbacks that raise notices mid-turn.</summary>
  internal static AgentSessionViewModel CreateViewModel(TurnRunner runner, bool marshalToUIThread = false)
  {
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

  /// <summary>Runner that parks on its cancellation token until released or cancelled,
  /// then returns a TurnCancelled failure (the domain contract for interruption).
  /// Shared by tests that need a genuinely busy turn.</summary>
  internal sealed class ParkingRunner
  {
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CancellationToken ObservedToken { get; private set; }

    public Task Started => _started.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

    public void Release() => _release.TrySetResult();

    // IDE0060: parameters required to match the TurnRunner delegate shape; values ignored.
#pragma warning disable IDE0060, S1172 // Delegate-shape parameters are unused by design.
    public async Task<Result<string>> RunAsync(SendMessageCommand _command, CancellationToken ct,
        TurnCallbacks? __ = null, Action<string>? ___ = null)
    {
      ObservedToken = ct;
      _ = _started.TrySetResult();
      Task finished = await Task.WhenAny(_release.Task, Task.Delay(Timeout.InfiniteTimeSpan, ct)).ConfigureAwait(true);
      if (finished == _release.Task)
      {
        return Result.Success("done");
      }

      try
      {
        await finished.ConfigureAwait(true);
      }
#pragma warning disable S108 // Deliberate: stop cancelled the turn; the Result below reports it.
      catch (OperationCanceledException) { }
#pragma warning restore S108
      return Result.Failure<string>(new DomainError("TurnCancelled", "interrupted."));
    }
#pragma warning restore IDE0060, S1172 // Delegate-shape parameters are unused by design.
  }
}
