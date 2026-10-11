using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;

namespace eThangAgent.Composition.Tests;

/// <summary>The workspace-prior I/O writer over the State Domain's store: a round-trip
/// through the persisted JSON, a missing key and a corrupt value both degrading to a
/// null prior, and a best-effort write that swallows a store fault.</summary>
public class MeasuredMixPriorWriterTests
{
  private const string Workspace = "C:/workspaces/demo";
  private static readonly string Key = MeasuredMixPrior.StateKey("openrouter", Workspace);

  private static MeasuredMixPrior SamplePrior() => new(
      TotalInput: 1000L, TotalOutput: 4000L, TurnCount: 7,
      UpdatedAt: new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

  [Fact]
  public async Task WriteThenRead_RoundTrips()
  {
    InMemoryStateStore store = new();
    MeasuredMixPriorWriter writer = new(store, Key);
    MeasuredMixPrior prior = SamplePrior();

    await writer.WriteAsync(prior, TestContext.Current.CancellationToken);
    MeasuredMixPrior? read = await writer.ReadAsync(TestContext.Current.CancellationToken);

    Assert.Equal(prior, read);
  }

  [Fact]
  public async Task Read_MissingKey_ReturnsNull()
  {
    InMemoryStateStore store = new();
    MeasuredMixPriorWriter writer = new(store, Key);

    MeasuredMixPrior? read = await writer.ReadAsync(TestContext.Current.CancellationToken);

    Assert.Null(read);
  }

  [Fact]
  public async Task Read_CorruptValue_ReturnsNull()
  {
    InMemoryStateStore store = new();
    _ = await store.SetKeyCasAsync(Workspace, "measured_mix", $"openrouter:{Workspace}", "{ not json", null, TestContext.Current.CancellationToken);
    MeasuredMixPriorWriter writer = new(store, Key);

    MeasuredMixPrior? read = await writer.ReadAsync(TestContext.Current.CancellationToken);

    Assert.Null(read);
  }

  [Fact]
  public async Task Write_FaultSwallowed()
  {
    ThrowingStateStore store = new();
    MeasuredMixPriorWriter writer = new(store, Key);

    await writer.WriteAsync(SamplePrior(), TestContext.Current.CancellationToken);

    Assert.True(store.Attempted);
  }

  /// <summary>Minimal in-memory IStateStore: the writer's get/set surface plus inert
  /// stubs for the rest of the interface.</summary>
  private sealed class InMemoryStateStore : IStateStore
  {
    private readonly Dictionary<string, StateKeyValue> _keys = new(StringComparer.Ordinal);

    public Task<StateKeyValue?> GetKeyAsync(string workspaceId, string ns, string name, CancellationToken ct = default)
        => Task.FromResult(_keys.TryGetValue($"{workspaceId}|{ns}|{name}", out StateKeyValue? kv) ? kv : null);

    public Task<IReadOnlyList<StateKeyValue>> ListKeysAsync(string workspaceId, string? ns, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<StateKeyValue>>([.. _keys.Values]);

    public Task<StateKeyValue?> SetKeyCasAsync(string workspaceId, string ns, string name, string value,
        int? expectedVersion, CancellationToken ct = default)
    {
      StateKeyValue row = new(ns, name, value, 1);
      _keys[$"{workspaceId}|{ns}|{name}"] = row;
      return Task.FromResult<StateKeyValue?>(row);
    }

    public Task<bool> DeleteKeyCasAsync(string workspaceId, string ns, string name, int? expectedVersion, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<int> DeleteNamespacePrefixAsync(string workspaceId, string nsPrefix, CancellationToken ct = default)
        => Task.FromResult(0);

    public Task<Result<IReadOnlyList<StateSearchHit>>> SearchKeysAsync(string workspaceId, string query, int limit, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<StateSearchHit>>([]));

    public Task<TransitionRecord> InsertTransitionAsync(string workspaceId, TransitionRecord transition, CancellationToken ct = default)
        => Task.FromResult(transition);

    public Task<IReadOnlyList<TransitionRecord>> GetTransitionsAsync(string workspaceId, IReadOnlyList<string> transitionIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TransitionRecord>>([]);

    public Task SetTransitionStatusAsync(string workspaceId, string transitionId, string status, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task AppendEventAsync(string workspaceId, string kind, string payloadJson, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<StateEvent>> GetEventsAsync(string workspaceId, int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<StateEvent>>([]);
  }

  /// <summary>A store whose every write throws: the writer's best-effort contract must
  /// swallow the fault.</summary>
  private sealed class ThrowingStateStore : IStateStore
  {
    public bool Attempted { get; private set; }

    public Task<StateKeyValue?> GetKeyAsync(string workspaceId, string ns, string name, CancellationToken ct = default)
        => Task.FromResult<StateKeyValue?>(null);

    public Task<IReadOnlyList<StateKeyValue>> ListKeysAsync(string workspaceId, string? ns, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<StateKeyValue>>([]);

    public Task<StateKeyValue?> SetKeyCasAsync(string workspaceId, string ns, string name, string value,
        int? expectedVersion, CancellationToken ct = default)
    {
      Attempted = true;
      throw new InvalidOperationException("store fault");
    }

    public Task<bool> DeleteKeyCasAsync(string workspaceId, string ns, string name, int? expectedVersion, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<int> DeleteNamespacePrefixAsync(string workspaceId, string nsPrefix, CancellationToken ct = default)
        => Task.FromResult(0);

    public Task<Result<IReadOnlyList<StateSearchHit>>> SearchKeysAsync(string workspaceId, string query, int limit, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<StateSearchHit>>([]));

    public Task<TransitionRecord> InsertTransitionAsync(string workspaceId, TransitionRecord transition, CancellationToken ct = default)
        => Task.FromResult(transition);

    public Task<IReadOnlyList<TransitionRecord>> GetTransitionsAsync(string workspaceId, IReadOnlyList<string> transitionIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TransitionRecord>>([]);

    public Task SetTransitionStatusAsync(string workspaceId, string transitionId, string status, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task AppendEventAsync(string workspaceId, string kind, string payloadJson, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<StateEvent>> GetEventsAsync(string workspaceId, int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<StateEvent>>([]);
  }
}
