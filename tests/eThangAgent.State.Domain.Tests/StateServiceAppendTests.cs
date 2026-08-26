using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;

namespace eThangAgent.State.Domain.Tests;

/// <summary>Append semantics: atomic CAS append-line for ledgers, and bulk prefix
/// delete for SDD scratch cleanup. Both flow through the existing store seam.</summary>
public class StateServiceAppendTests
{
  private readonly FakeAppendStore _store = new();
  private StateService Service() => new(_store, new FakeEvidenceRunner(), new FixedWs("ws1"));

  [Fact]
  public async Task Append_ToExistingKey_AddsLine_AndBumpsVersion()
  {
    _store.Keys["ws1|sdd.x/ledger"] = new StateKeyValue("sdd.x", "ledger", "line one", 3);

    Result<StateKeyValue> r = await Service().AppendAsync("sdd.x/ledger", "line two", expectedVersion: 3);

    Assert.True(r.IsSuccess);
    Assert.Equal(4, r.Value!.Version);
    Assert.Equal("line one\nline two", r.Value.Value);
  }

  [Fact]
  public async Task Append_ToMissingKey_CreatesWithSingleLine()
  {
    Result<StateKeyValue> r = await Service().AppendAsync("sdd.y/ledger", "first line", null);
    Assert.True(r.IsSuccess);
    Assert.Equal(1, r.Value!.Version);
    Assert.Equal("first line", r.Value.Value);
  }

  [Fact]
  public async Task Append_VersionConflict_FailsClosed()
  {
    _store.Conflict = true;
    Result<StateKeyValue> r = await Service().AppendAsync("sdd.x/ledger", "line", expectedVersion: 9);
    Assert.False(r.IsSuccess);
    Assert.Equal("VersionConflict", r.Error!.Code);
  }

  [Theory]
  [InlineData("")]
  [InlineData("   ")]
  [InlineData("multi\nline")]
  public async Task Append_InvalidText_Fails(string text)
  {
    Result<StateKeyValue> r = await Service().AppendAsync("sdd.x/ledger", text, null);
    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidText", r.Error!.Code);
  }

  [Fact]
  public async Task DeletePrefix_RemovesOnlyMatchingNamespace()
  {
    _store.Keys["ws1|sdd.alpha/a"] = new StateKeyValue("sdd.alpha", "a", "v", 1);
    _store.Keys["ws1|sdd.alphabeta/b"] = new StateKeyValue("sdd.alphabeta", "b", "v", 1);
    _store.Keys["ws1|sdd.other/c"] = new StateKeyValue("sdd.other", "c", "v", 1);

    Result<int> r = await Service().DeletePrefixAsync("sdd.alpha");
    Assert.True(r.IsSuccess);
    Assert.Equal(1, r.Value); // dotted boundary: sdd.alpha yes, sdd.alphabeta NO

    // dotted boundary respected: sdd.alphabeta survives; sdd.other untouched
    Assert.Contains(_store.Keys, k => k.Key == "ws1|sdd.alphabeta/b");
    Assert.Contains(_store.Keys, k => k.Key == "ws1|sdd.other/c");
  }

  [Fact]
  public async Task DeletePrefix_RejectsMalformedAndReserved()
  {
    Assert.Equal("InvalidKey", (await Service().DeletePrefixAsync("has/slash")).Error!.Code);
    Assert.Equal("InvalidKey", (await Service().DeletePrefixAsync("has space")).Error!.Code);
    Assert.Equal("ReservedNamespace", (await Service().DeletePrefixAsync("todo")).Error!.Code);
    Assert.Equal("ReservedNamespace", (await Service().DeletePrefixAsync("current")).Error!.Code);
  }

  private sealed class FakeAppendStore : IStateStore
  {
    public Dictionary<string, StateKeyValue> Keys { get; } = [];
    public StateKeyValue? Existing { get; set; }
    public bool Conflict { get; set; }

    public Task<StateKeyValue?> GetKeyAsync(string workspaceId, string ns, string name, CancellationToken ct = default)
        => Task.FromResult(Keys.TryGetValue($"{workspaceId}|{ns}/{name}", out StateKeyValue? row) ? row : null);

    public Task<IReadOnlyList<StateKeyValue>> ListKeysAsync(string workspaceId, string? ns, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<StateKeyValue>>(
            [.. Keys.Where(k => k.Key.StartsWith(workspaceId + "|", StringComparison.Ordinal)).Select(k => k.Value)]);

    public Task<StateKeyValue?> SetKeyCasAsync(string workspaceId, string ns, string name,
        string value, int? expectedVersion, CancellationToken ct = default)
    {
      if (Conflict)
      {
        return Task.FromResult<StateKeyValue?>(null);
      }

      string key = $"{workspaceId}|{ns}/{name}";
      int version = Keys.TryGetValue(key, out StateKeyValue? row) ? row.Version + 1 : 1;
      StateKeyValue saved = new(ns, name, value, version);
      Keys[key] = saved;
      return Task.FromResult<StateKeyValue?>(saved);
    }

    public Task<bool> DeleteKeyCasAsync(string workspaceId, string ns, string name, int? expectedVersion, CancellationToken ct = default)
        => Task.FromResult(Keys.Remove($"{workspaceId}|{ns}"));

    public Task<int> DeleteNamespacePrefixAsync(string workspaceId, string nsPrefix, CancellationToken ct = default)
    {
      int removed = 0;
      foreach (string? key in Keys.Keys.Where(k => k.StartsWith(workspaceId + "|", StringComparison.Ordinal) &&
                                              (k[(workspaceId.Length + 1)..].Split('/')[0] == nsPrefix ||
                                               k[(workspaceId.Length + 1)..].Split('/')[0].StartsWith(nsPrefix + ".", StringComparison.Ordinal))).ToList())
      {
        if (Keys.Remove(key))
        {
          removed++;
        }
      }
      return Task.FromResult(removed);
    }

    public Task<Result<IReadOnlyList<StateSearchHit>>> SearchKeysAsync(string workspaceId, string query, int limit, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<StateSearchHit>>([]));

    public Task<TransitionRecord> InsertTransitionAsync(string workspaceId, TransitionRecord transition, CancellationToken ct = default)
        => Task.FromResult(transition);

    public Task<IReadOnlyList<TransitionRecord>> GetTransitionsAsync(string workspaceId, IReadOnlyList<string> ids, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TransitionRecord>>([]);

    public Task SetTransitionStatusAsync(string workspaceId, string transitionId, string status, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task AppendEventAsync(string workspaceId, string kind, string payloadJson, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<StateEvent>> GetEventsAsync(string workspaceId, int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<StateEvent>>([]);
  }

  private sealed class FakeEvidenceRunner : IEvidenceRunner
  {
    public Task<EvidenceResult> RunAsync(string command, CancellationToken ct = default)
        => Task.FromResult(new EvidenceResult(command, true, ""));
  }

  private sealed class FixedWs(string id) : IWorkspaceContext
  {
    public string WorkspaceId { get; } = id;
  }
}
