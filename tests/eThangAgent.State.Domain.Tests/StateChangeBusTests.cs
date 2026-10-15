using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;

namespace eThangAgent.State.Domain.Tests;

/// <summary>State-mutation events: publish on set/append/delete, nothing on failures,
///     and the no-stream wiring stays byte-identical. The in-process stream's fan-out,
///     fault containment, and lease disposal are pinned here too.</summary>
public class StateChangeBusTests
{
  private static StateService Create(FakeStateStore? store = null, IStateChangeBus? changes = null)
      => new(store ?? new FakeStateStore(), new FakeRunner(), new StubWorkspace(), changes);

  private sealed class StubWorkspace : IWorkspaceContext
  {
    public string WorkspaceId => "ws";
  }

  private sealed class FakeRunner(Func<string, EvidenceResult>? respond = null) : IEvidenceRunner
  {
    private readonly Func<string, EvidenceResult> _respond = respond ?? (command => new EvidenceResult(command, true, ""));

    public Task<EvidenceResult> RunAsync(string command, CancellationToken ct = default)
        => Task.FromResult(_respond(command));
  }

  private sealed class FakeStateStore : IStateStore
  {
    public List<string> OperationLog { get; } = [];
    private readonly Dictionary<string, StateKeyValue> _keys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TransitionRecord> _transitions = new(StringComparer.Ordinal);
    private readonly List<StateEvent> _events = [];
    private long _eventSeq;


    public Task<StateKeyValue?> GetKeyAsync(string workspaceId, string ns, string name, CancellationToken ct = default)
        => Task.FromResult(_keys.TryGetValue($"{ns}/{name}", out StateKeyValue? kv) ? kv : null);

    public Task<IReadOnlyList<StateKeyValue>> ListKeysAsync(string workspaceId, string? ns, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<StateKeyValue>>([.. _keys.Values.Where(k => ns is null || k.Ns == ns)]);

    public Task<StateKeyValue?> SetKeyCasAsync(string workspaceId, string ns, string name, string value,
        int? expectedVersion, CancellationToken ct = default)
    {
      string id = $"{ns}/{name}";
      _ = _keys.TryGetValue(id, out StateKeyValue? existing);
      if (expectedVersion.HasValue && (existing is null || existing.Version != expectedVersion.Value))
      {
        return Task.FromResult<StateKeyValue?>(null);
      }

      StateKeyValue row = new(ns, name, value, (existing?.Version ?? 0) + 1);
      _keys[id] = row;
      return Task.FromResult<StateKeyValue?>(row);
    }

    public Task<bool> DeleteKeyCasAsync(string workspaceId, string ns, string name,
        int? expectedVersion, CancellationToken ct = default)
    {
      string id = $"{ns}/{name}";
      if (!_keys.TryGetValue(id, out StateKeyValue? existing))
      {
        return Task.FromResult(false);
      }

      if (expectedVersion.HasValue && existing.Version != expectedVersion.Value)
      {
        return Task.FromResult(false);
      }

      _ = _keys.Remove(id);
      OperationLog.Add($"delete:{id}");
      return Task.FromResult(true);
    }

    public Task<int> DeleteNamespacePrefixAsync(string workspaceId, string nsPrefix, CancellationToken ct = default)
=> Task.FromResult(0);

    public Task<Result<IReadOnlyList<StateSearchHit>>> SearchKeysAsync(string workspaceId, string query, int limit, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<StateSearchHit>>([]));

    public Task<TransitionRecord> InsertTransitionAsync(string workspaceId, TransitionRecord transition, CancellationToken ct = default)
    {
      _transitions[transition.Id] = transition;
      OperationLog.Add("insert-transition");
      return Task.FromResult(transition);
    }

    public Task<IReadOnlyList<TransitionRecord>> GetTransitionsAsync(string workspaceId,
        IReadOnlyList<string> transitionIds, CancellationToken ct = default)
    {
      List<TransitionRecord> selected = transitionIds.Count == 0
                ? [.. _transitions.Values.Where(t => t.Status == "pending")]
                : [.. transitionIds.Where(_transitions.ContainsKey).Select(t => _transitions[t])];
      return Task.FromResult<IReadOnlyList<TransitionRecord>>(selected);
    }

    public Task SetTransitionStatusAsync(string workspaceId, string transitionId, string status, CancellationToken ct = default)
    {
      OperationLog.Add($"status:{transitionId}:{status}");
      if (_transitions.TryGetValue(transitionId, out TransitionRecord? t))
      {
        _transitions[transitionId] = t with { Status = status };
      }

      return Task.CompletedTask;
    }

    public Task AppendEventAsync(string workspaceId, string kind, string payloadJson, CancellationToken ct = default)
    {
      OperationLog.Add($"event:{kind}");
      _events.Add(new StateEvent(++_eventSeq, kind, payloadJson, DateTimeOffset.UtcNow));
      return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StateEvent>> GetEventsAsync(string workspaceId, int limit, CancellationToken ct = default)
    {
      int take = Math.Min(limit, _events.Count);
      List<StateEvent> newest = [.. _events.Skip(_events.Count - take).Reverse()];
      return Task.FromResult<IReadOnlyList<StateEvent>>(newest);
    }
  }

  [Fact]
  public async Task Set_PublishesStateChange()
  {
    InProcessStateChangeBus stream = new();
    List<StateChange> seen = [];
    using IDisposable lease = stream.Subscribe(seen.Add);

    _ = await Create(changes: stream).SetAsync("current/head", "done", null, ct: TestContext.Current.CancellationToken);

    StateChange change = Assert.Single(seen);
    Assert.Equal(StateChangeKind.Set, change.Kind);
    Assert.Equal("current/head", change.Key);
  }

  [Fact]
  public async Task Append_PublishesStateChange()
  {
    InProcessStateChangeBus stream = new();
    List<StateChange> seen = [];
    using IDisposable lease = stream.Subscribe(seen.Add);
    StateService service = Create(changes: stream);
    _ = await service.SetAsync("log/lines", "one", null, ct: TestContext.Current.CancellationToken);

    _ = await service.AppendAsync("log/lines", "two", null, ct: TestContext.Current.CancellationToken);

    Assert.Equal(2, seen.Count); // the create-Set and the append-Set
    Assert.All(seen, c => Assert.Equal(StateChangeKind.Set, c.Kind)); // append flows through SetAsync
  }

  [Fact]
  public async Task Delete_PublishesStateChange()
  {
    InProcessStateChangeBus stream = new();
    List<StateChange> seen = [];
    using IDisposable lease = stream.Subscribe(seen.Add);
    StateService service = Create(changes: stream);
    _ = await service.SetAsync("current/head", "done", null, ct: TestContext.Current.CancellationToken);

    _ = await service.DeleteAsync("current/head", 1, ct: TestContext.Current.CancellationToken);

    Assert.Equal(2, seen.Count);
    Assert.Equal(StateChangeKind.Delete, seen[1].Kind);
  }

  [Fact]
  public async Task FailedWrite_PublishesNothing()
  {
    InProcessStateChangeBus stream = new();
    List<StateChange> seen = [];
    using IDisposable lease = stream.Subscribe(seen.Add);
    FakeStateStore store = new();
    StateService service = Create(store, stream);
    _ = await service.SetAsync("current/head", "done", null, ct: TestContext.Current.CancellationToken);

    Result<StateKeyValue> conflict = await service.SetAsync("current/head", "other", 99, ct: TestContext.Current.CancellationToken);

    Assert.False(conflict.IsSuccess);
    _ = Assert.Single(seen); // only the successful set
  }

  [Fact]
  public async Task NoStreamWired_NoCrash()
  {
    StateService service = Create();

    Result<StateKeyValue> result = await service.SetAsync("current/head", "done", null, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
  }

  [Fact]
  public void Publish_SubscriberFault_IsContained_OthersNotified()
  {
    List<string> faults = [];
    InProcessStateChangeBus stream = new(faultLog: faults.Add);
    List<StateChange> healthy = [];
    using IDisposable bad = stream.Subscribe(_ => throw new InvalidOperationException("boom"));
    using IDisposable good = stream.Subscribe(healthy.Add);

    stream.Publish(new StateChange("ws", "k", StateChangeKind.Set, null, DateTimeOffset.UtcNow));

    _ = Assert.Single(healthy);
    _ = Assert.Single(faults);
  }

  [Fact]
  public void Subscribe_Dispose_StopsDelivery()
  {
    InProcessStateChangeBus stream = new();
    List<StateChange> seen = [];
    IDisposable lease = stream.Subscribe(seen.Add);

    lease.Dispose();
    stream.Publish(new StateChange("ws", "k", StateChangeKind.Set, null, DateTimeOffset.UtcNow));

    Assert.Empty(seen);
  }
}
