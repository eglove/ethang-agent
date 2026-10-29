using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;

namespace eThangAgent.State.Domain.Tests;

public class StateServiceTests
{
  private static StateService Create(FakeStateStore? store = null, FakeRunner? runner = null)
      => new(store ?? new FakeStateStore(), runner ?? new FakeRunner(), new StubWorkspace());

  [Fact]
  public async Task Set_StaleVersion_Fails_NamingCurrentVersion()
  {
    FakeStateStore store = new();
    _ = await Create(store).SetAsync("current/head", "done", null, ct: TestContext.Current.CancellationToken);

    Result<StateKeyValue> result = await Create(store).SetAsync("current/head", "other", 5, ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("VersionConflict", result.Error.Code);
    Assert.Contains("current version is 1", result.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Get_MissingKey_Fails_KeyNotFound()
  {
    Result<string> result = await Create().GetAsync("current/head", ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsSuccess);
    Assert.Equal("KeyNotFound", result.Error.Code);
  }

  /// <summary>Issue #310: the todo tool's namespace is gone, so its reservation is too.
  ///     These two tests now pin the ORDINARY namespace contract at the service, which is
  ///     what any other caller gets.</summary>
  [Fact]
  public async Task Set_AllowsAnOrdinaryNamespace()
  {
    Result<StateKeyValue> result = await Create().SetAsync("notes/scratch", "[]", null, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("notes", result.Value.Ns);
    Assert.Equal(1, result.Value.Version);
  }

  [Fact]
  public async Task Delete_AllowsAnOrdinaryNamespace_AfterSet()
  {
    StateService service = Create();
    _ = await service.SetAsync("notes/scratch", "[]", null, ct: TestContext.Current.CancellationToken);

    Result<string> deleted = await service.DeleteAsync("notes/scratch", 1, ct: TestContext.Current.CancellationToken);

    Assert.True(deleted.IsSuccess);
    Assert.Equal("deleted notes/scratch", deleted.Value);
  }

  [Fact]
  public async Task Transition_AssignsId_AppendsAttachedEvent()
  {
    FakeStateStore store = new();

    Result<string> result = await Create(store).TransitionAsync("coding", "done", "work", ["Write-Output ok"], ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.StartsWith("tr-", result.Value, StringComparison.Ordinal);
    Assert.Contains("transition.attached", store.EventKinds);
    Assert.Equal("pending", store.Transitions.Single().Status);
  }

  [Fact]
  public async Task Verify_AllConfirm_Certifies_AndPersistsHeadCertificate()
  {
    FakeStateStore store = new();
    StateService service = Create(store, new FakeRunner(_ => new EvidenceResult("cmd", true, "")));
    _ = await service.SetAsync("current/head", "done", null, ct: TestContext.Current.CancellationToken);
    _ = await service.TransitionAsync("coding", "done", "work", ["Write-Output ok"], ct: TestContext.Current.CancellationToken);

    CertificationReport report = await service.VerifyAsync(null, ct: TestContext.Current.CancellationToken);

    Assert.True(report.Certified);
    Assert.False(report.Violated);
    Assert.Equal("certified", store.Transitions.Single().Status);
    Assert.Contains("state.certified", store.EventKinds);
    StateKeyValue? certificate = await store.GetKeyAsync("ws", "current", "certificate", ct: TestContext.Current.CancellationToken);
    Assert.NotNull(certificate);
  }

  [Fact]
  public async Task Verify_FailingEvidence_Violates_AndRevokesHeadCertificateFirst()
  {
    FakeStateStore store = new();
    Queue<bool> outcomes = new([true, false]); // certify first, then fail on re-verification
    StateService service = Create(store, new FakeRunner(_ =>
        {
          bool confirmed = outcomes.Dequeue();
          return new EvidenceResult("cmd", confirmed, confirmed ? "" : "exit 1");
        }));
    _ = await service.SetAsync("current/head", "done", null, ct: TestContext.Current.CancellationToken);
    Result<string> id = await service.TransitionAsync("coding", "done", "work", ["Write-Output ok"], ct: TestContext.Current.CancellationToken);
    _ = await service.VerifyAsync(null, ct: TestContext.Current.CancellationToken); // certify first
    store.OperationLog.Clear();

    CertificationReport report = await service.VerifyAsync([id.Value!], ct: TestContext.Current.CancellationToken);

    Assert.False(report.Certified);
    Assert.True(report.Violated);
    Assert.Contains("exit 1", report.BlockingReasons.Single(), StringComparison.Ordinal);
    Assert.Equal("violated", store.Transitions.Single().Status);
    Assert.Null(await store.GetKeyAsync("ws", "current", "certificate", ct: TestContext.Current.CancellationToken));
    int revokeIndex = store.OperationLog.IndexOf("delete:current/certificate");
    int violatedIndex = store.OperationLog.IndexOf("event:state.violated");
    Assert.True(revokeIndex >= 0 && violatedIndex > revokeIndex, "certificate must be revoked before the violated event");
  }

  [Fact]
  public async Task Verify_EmptyEvidence_FailsClosed()
  {
    FakeStateStore store = new();
    StateService service = Create(store);
    _ = await service.TransitionAsync("coding", "done", "work", [], ct: TestContext.Current.CancellationToken);

    CertificationReport report = await service.VerifyAsync(null, ct: TestContext.Current.CancellationToken);

    Assert.False(report.Certified);
    Assert.Contains("no attached evidence", report.BlockingReasons.Single(), StringComparison.Ordinal);
  }

  [Fact]
  public async Task Verify_NothingSelected_FailsClosed()
  {
    CertificationReport report = await Create().VerifyAsync(null, ct: TestContext.Current.CancellationToken);

    Assert.False(report.Certified);
    Assert.Contains("No transitions selected", report.BlockingReasons.Single(), StringComparison.Ordinal);
  }

  [Fact]
  public async Task Verify_MissingRequestedId_ListedInBlocking()
  {
    CertificationReport report = await Create().VerifyAsync(["tr-missing"], ct: TestContext.Current.CancellationToken);

    Assert.False(report.Certified);
    Assert.Contains("Missing transition: tr-missing.", report.BlockingReasons.Single(), StringComparison.Ordinal);
  }

  // goal/check holds SHELL COMMANDS, not C# expressions (issue #283), so this test now
  // asserts the runner seam rather than the evidence runner. The full command semantics
  // live in CheckGoalCommandTests; what matters here is that the run stays REPORT-ONLY -
  // no events, no transitions, no certification side effects.
  [Fact]
  public async Task CheckGoal_RunsCommands_ReportOnly()
  {
    FakeStateStore store = new();
    StateService service = new(store, new FakeRunner(_ => new EvidenceResult("cmd", true, "")),
        new StubWorkspace(), goalRunner: new AlwaysPassGoalRunner());
    _ = await service.SetAsync("goal/check", "[\"Write-Output ok\"]", null, ct: TestContext.Current.CancellationToken);

    CertificationReport report = await service.CheckGoalAsync(TestContext.Current.CancellationToken);

    Assert.True(report.Certified);
    Assert.Empty(store.EventKinds);
    Assert.Empty(store.Transitions);
  }

  private sealed class AlwaysPassGoalRunner : IGoalCommandRunner
  {
    public Task<EvidenceResult> RunAsync(string workingDirectory, string command,
        CancellationToken ct = default) =>
        Task.FromResult(new EvidenceResult(command, true, string.Empty));
  }

  [Fact]
  public async Task History_ReplaysEvents()
  {
    FakeStateStore store = new();
    StateService service = Create(store);
    _ = await service.SetAsync("current/head", "done", null, ct: TestContext.Current.CancellationToken);
    _ = await service.TransitionAsync("a", "b", "s", [], ct: TestContext.Current.CancellationToken);

    Result<IReadOnlyList<string>> result = await service.HistoryAsync(20, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    _ = Assert.Single(result.Value);
  }

  /// <summary>A workspace whose id IS its path - the pre-#317 shape, which is what a headless
  ///     stub has (issue #317 D5 made the two separate facts on the real context).</summary>
  private sealed class StubWorkspace : IWorkspaceContext
  {
    public string WorkspaceId => "ws";

    public string WorkspaceRoot => "ws";
  }

  private sealed class FakeRunner(Func<string, EvidenceResult>? respond = null) : IEvidenceRunner
  {
    private readonly Func<string, EvidenceResult> _respond = respond ?? (command => new EvidenceResult(command, true, ""));

    public Task<EvidenceResult> RunAsync(string command, CancellationToken ct = default)
        => Task.FromResult(_respond(command));
  }

  private sealed class FakeStateStore : IStateStore
  {
    private readonly Dictionary<string, StateKeyValue> _keys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TransitionRecord> _transitions = new(StringComparer.Ordinal);
    private readonly List<StateEvent> _events = [];
    private long _eventSeq;

    public List<string> OperationLog { get; } = [];
    public List<string> EventKinds => [.. _events.Select(e => e.Kind)];
    public IReadOnlyCollection<TransitionRecord> Transitions => _transitions.Values;

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
}
