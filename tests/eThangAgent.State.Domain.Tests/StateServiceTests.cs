using eThangAgent.CapabilityDomain;
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
        var store = new FakeStateStore();
        await Create(store).SetAsync("current/head", "done", null);

        var result = await Create(store).SetAsync("current/head", "other", 5);

        Assert.False(result.IsSuccess);
        Assert.Equal("VersionConflict", result.Error!.Code);
        Assert.Contains("current version is 1", result.Error.Message);
    }

    [Fact]
    public async Task Get_MissingKey_Fails_KeyNotFound()
    {
        var result = await Create().GetAsync("current/head");
        Assert.False(result.IsSuccess);
        Assert.Equal("KeyNotFound", result.Error!.Code);
    }

    [Fact]
    public async Task Set_AllowsTodoNamespace_ForInternalComposition()
    {
        var result = await Create().SetAsync("todo/list", "[]", null);

        Assert.True(result.IsSuccess);
        Assert.Equal("todo", result.Value!.Ns);
        Assert.Equal(1, result.Value.Version);
    }

    [Fact]
    public async Task Delete_AllowsTodoNamespace_AfterSet()
    {
        var service = Create();
        await service.SetAsync("todo/list", "[]", null);

        var deleted = await service.DeleteAsync("todo/list", 1);

        Assert.True(deleted.IsSuccess);
        Assert.Equal("deleted todo/list", deleted.Value);
    }

    [Fact]
    public async Task Transition_AssignsId_AppendsAttachedEvent()
    {
        var store = new FakeStateStore();

        var result = await Create(store).TransitionAsync("coding", "done", "work", ["Write-Output ok"]);

        Assert.True(result.IsSuccess);
        Assert.StartsWith("tr-", result.Value);
        Assert.Contains("transition.attached", store.EventKinds);
        Assert.Equal("pending", store.Transitions.Single().Status);
    }

    [Fact]
    public async Task Verify_AllConfirm_Certifies_AndPersistsHeadCertificate()
    {
        var store = new FakeStateStore();
        var service = Create(store, new FakeRunner(_ => new EvidenceResult("cmd", true, "")));
        await service.SetAsync("current/head", "done", null);
        await service.TransitionAsync("coding", "done", "work", ["Write-Output ok"]);

        var report = await service.VerifyAsync(null);

        Assert.True(report.Certified);
        Assert.False(report.Violated);
        Assert.Equal("certified", store.Transitions.Single().Status);
        Assert.Contains("state.certified", store.EventKinds);
        var certificate = await store.GetKeyAsync("ws", "current", "certificate");
        Assert.NotNull(certificate);
    }

    [Fact]
    public async Task Verify_FailingEvidence_Violates_AndRevokesHeadCertificateFirst()
    {
        var store = new FakeStateStore();
        var outcomes = new Queue<bool>([true, false]); // certify first, then fail on re-verification
        var service = Create(store, new FakeRunner(_ =>
        {
            var confirmed = outcomes.Dequeue();
            return new EvidenceResult("cmd", confirmed, confirmed ? "" : "exit 1");
        }));
        await service.SetAsync("current/head", "done", null);
        var id = await service.TransitionAsync("coding", "done", "work", ["Write-Output ok"]);
        await service.VerifyAsync(null); // certify first
        store.OperationLog.Clear();

        var report = await service.VerifyAsync([id.Value!]);

        Assert.False(report.Certified);
        Assert.True(report.Violated);
        Assert.Contains("exit 1", report.BlockingReasons.Single());
        Assert.Equal("violated", store.Transitions.Single().Status);
        Assert.Null(await store.GetKeyAsync("ws", "current", "certificate"));
        var revokeIndex = store.OperationLog.IndexOf("delete:current/certificate");
        var violatedIndex = store.OperationLog.IndexOf("event:state.violated");
        Assert.True(revokeIndex >= 0 && violatedIndex > revokeIndex, "certificate must be revoked before the violated event");
    }

    [Fact]
    public async Task Verify_EmptyEvidence_FailsClosed()
    {
        var store = new FakeStateStore();
        var service = Create(store);
        await service.TransitionAsync("coding", "done", "work", []);

        var report = await service.VerifyAsync(null);

        Assert.False(report.Certified);
        Assert.Contains("no attached evidence", report.BlockingReasons.Single());
    }

    [Fact]
    public async Task Verify_NothingSelected_FailsClosed()
    {
        var report = await Create().VerifyAsync(null);

        Assert.False(report.Certified);
        Assert.Contains("No transitions selected", report.BlockingReasons.Single());
    }

    [Fact]
    public async Task Verify_MissingRequestedId_ListedInBlocking()
    {
        var report = await Create().VerifyAsync(["tr-missing"]);

        Assert.False(report.Certified);
        Assert.Contains("Missing transition: tr-missing.", report.BlockingReasons.Single());
    }

    [Fact]
    public async Task CheckGoal_RunsCommands_ReportOnly()
    {
        var store = new FakeStateStore();
        var service = Create(store, new FakeRunner(_ => new EvidenceResult("cmd", true, "")));
        await service.SetAsync("goal/check", "[\"Write-Output ok\"]", null);

        var report = await service.CheckGoalAsync();

        Assert.True(report.Certified);
        Assert.Empty(store.EventKinds);
        Assert.Empty(store.Transitions);
    }

    [Fact]
    public async Task History_ReplaysEvents()
    {
        var store = new FakeStateStore();
        var service = Create(store);
        await service.SetAsync("current/head", "done", null);
        await service.TransitionAsync("a", "b", "s", []);

        var result = await service.HistoryAsync(20);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!);
    }

    private sealed class StubWorkspace : IWorkspaceContext
    {
        public string WorkspaceId => "ws";
    }

    private sealed class FakeRunner : IEvidenceRunner
    {
        private readonly Func<string, EvidenceResult> _respond;
        public FakeRunner(Func<string, EvidenceResult>? respond = null)
            => _respond = respond ?? (command => new EvidenceResult(command, true, ""));
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
        public List<string> EventKinds => _events.Select(e => e.Kind).ToList();
        public IReadOnlyCollection<TransitionRecord> Transitions => _transitions.Values;

        public Task<StateKeyValue?> GetKeyAsync(string workspaceId, string ns, string name, CancellationToken ct = default)
            => Task.FromResult(_keys.TryGetValue($"{ns}/{name}", out var kv) ? kv : null);

        public Task<IReadOnlyList<StateKeyValue>> ListKeysAsync(string workspaceId, string? ns, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<StateKeyValue>>(_keys.Values
                .Where(k => ns is null || k.Ns == ns).ToList());

        public Task<StateKeyValue?> SetKeyCasAsync(string workspaceId, string ns, string name, string value,
            int? expectedVersion, CancellationToken ct = default)
        {
            var id = $"{ns}/{name}";
            _keys.TryGetValue(id, out var existing);
            if (expectedVersion.HasValue && (existing is null || existing.Version != expectedVersion.Value))
                return Task.FromResult<StateKeyValue?>(null);
            var row = new StateKeyValue(ns, name, value, (existing?.Version ?? 0) + 1);
            _keys[id] = row;
            return Task.FromResult<StateKeyValue?>(row);
        }

        public Task<bool> DeleteKeyCasAsync(string workspaceId, string ns, string name,
            int? expectedVersion, CancellationToken ct = default)
        {
            var id = $"{ns}/{name}";
            if (!_keys.TryGetValue(id, out var existing)) return Task.FromResult(false);
            if (expectedVersion.HasValue && existing.Version != expectedVersion.Value) return Task.FromResult(false);
            _keys.Remove(id);
            OperationLog.Add($"delete:{id}");
            return Task.FromResult(true);
        }

                public Task<int> DeleteNamespacePrefixAsync(string workspaceId, string nsPrefix, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<Result<IReadOnlyList<StateSearchHit>>> SearchKeysAsync(string workspaceId, string query, int limit, CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<StateSearchHit>>.Success([]));

public Task<TransitionRecord> InsertTransitionAsync(string workspaceId, TransitionRecord transition, CancellationToken ct = default)
        {
            _transitions[transition.Id] = transition;
            OperationLog.Add("insert-transition");
            return Task.FromResult(transition);
        }

        public Task<IReadOnlyList<TransitionRecord>> GetTransitionsAsync(string workspaceId,
            IReadOnlyList<string> transitionIds, CancellationToken ct = default)
        {
            var selected = transitionIds.Count == 0
                ? _transitions.Values.Where(t => t.Status == "pending").ToList()
                : transitionIds.Where(_transitions.ContainsKey).Select(t => _transitions[t]).ToList();
            return Task.FromResult<IReadOnlyList<TransitionRecord>>(selected);
        }

        public Task SetTransitionStatusAsync(string workspaceId, string transitionId, string status, CancellationToken ct = default)
        {
            OperationLog.Add($"status:{transitionId}:{status}");
            if (_transitions.TryGetValue(transitionId, out var t))
                _transitions[transitionId] = t with { Status = status };
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
            var take = Math.Min(limit, _events.Count);
            var newest = _events.Skip(_events.Count - take).Reverse().ToList();
            return Task.FromResult<IReadOnlyList<StateEvent>>(newest);
        }
    }
}
