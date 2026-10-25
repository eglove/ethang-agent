using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.StateDomain.Tests;

// Issue #283 Part 2: goal/check holds SHELL COMMAND LINES, not C# expressions. The
// original implementation ran them through the evidence runner (which compiles C#), so a
// real gate command failed every time - measured 2026-10-25: storing ["dotnet --version"]
// made checkgoal report "The name 'dotnet' does not exist in the current context".
public class CheckGoalCommandTests
{
  private sealed class StubWorkspace : IWorkspaceContext
  {
    public string WorkspaceId => "ws";
  }

  /// <summary>Fails loudly if goal/check ever reaches the C# evidence runner again.</summary>
  private sealed class ForbiddenEvidenceRunner : IEvidenceRunner
  {
    public Task<EvidenceResult> RunAsync(string command, CancellationToken ct = default) =>
        throw new InvalidOperationException("goal/check must never reach the C# evidence runner");
  }

  private sealed class FakeGoalRunner(Func<string, EvidenceResult> respond) : IGoalCommandRunner
  {
    public List<string> Commands { get; } = [];

    public Task<EvidenceResult> RunAsync(string workingDirectory, string command,
        CancellationToken ct = default)
    {
      Commands.Add(command);
      return Task.FromResult(respond(command));
    }
  }

  private sealed class FakeSnapshot(WorkspaceSnapshot snapshot) : IWorkspaceSnapshotAccess
  {
    public Task<WorkspaceSnapshot> CaptureAsync(string workspaceRoot, CancellationToken ct = default) =>
        Task.FromResult(snapshot);
  }

  private sealed class FakeStateStore : IStateStore
  {
    private readonly Dictionary<string, StateKeyValue> _keys = new(StringComparer.Ordinal);

    public bool TryGetValue(string key, out string value)
    {
      if (_keys.TryGetValue(key, out StateKeyValue? row))
      {
        value = row.Value;
        return true;
      }

      value = string.Empty;
      return false;
    }

    public Task<StateKeyValue?> GetKeyAsync(string workspaceId, string ns, string name,
        CancellationToken ct = default) =>
        Task.FromResult(_keys.TryGetValue($"{ns}/{name}", out StateKeyValue? kv) ? kv : null);

    public Task<IReadOnlyList<StateKeyValue>> ListKeysAsync(string workspaceId, string? ns,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StateKeyValue>>(
            [.. _keys.Values.Where(k => ns is null || k.Ns == ns)]);

    public Task<StateKeyValue?> SetKeyCasAsync(string workspaceId, string ns, string name,
        string value, int? expectedVersion, CancellationToken ct = default)
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
        int? expectedVersion, CancellationToken ct = default) => Task.FromResult(false);

    public Task<int> DeleteNamespacePrefixAsync(string workspaceId, string nsPrefix,
        CancellationToken ct = default) => Task.FromResult(0);

    public Task<Result<IReadOnlyList<StateSearchHit>>> SearchKeysAsync(string workspaceId,
        string query, int limit, CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<StateSearchHit>>([]));

    public Task<TransitionRecord> InsertTransitionAsync(string workspaceId,
        TransitionRecord transition, CancellationToken ct = default) =>
        Task.FromResult(transition);

    public Task<IReadOnlyList<TransitionRecord>> GetTransitionsAsync(string workspaceId,
        IReadOnlyList<string> transitionIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TransitionRecord>>([]);

    public Task SetTransitionStatusAsync(string workspaceId, string transitionId, string status,
        CancellationToken ct = default) => Task.CompletedTask;

    public Task AppendEventAsync(string workspaceId, string kind, string payloadJson,
        CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<StateEvent>> GetEventsAsync(string workspaceId, int limit,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StateEvent>>([]);
  }

  private static WorkspaceSnapshot Snapshot() =>
      WorkspaceSnapshot.Create("ws",
          new Dictionary<string, WorkspaceFileFingerprint>(StringComparer.OrdinalIgnoreCase)
          {
            ["a.cs"] = new(1, DateTime.UtcNow, 1),
          });

  private static async Task<(StateService Service, FakeStateStore Store, FakeGoalRunner Runner)>
      CreateAsync(WorkspaceSnapshot? snapshot = null)
  {
    FakeStateStore store = new();
    FakeGoalRunner runner = new(c => new EvidenceResult(c, true, string.Empty));
    StateService service = new(store, new ForbiddenEvidenceRunner(), new StubWorkspace(),
        changes: null, goalRunner: runner,
        snapshots: snapshot is null ? null : new FakeSnapshot(snapshot));
    _ = await service.SetAsync("goal/check", "[\"dotnet build\"]", null,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    return (service, store, runner);
  }

  // The defect: the command must reach the SHELL runner, never the C# evidence runner.
  [Fact]
  public async Task CheckGoal_RunsCommandsThroughTheShellRunner()
  {
    (StateService service, _, FakeGoalRunner runner) = await CreateAsync();

    CertificationReport report = await service.CheckGoalAsync(TestContext.Current.CancellationToken);

    Assert.True(report.Certified);
    Assert.Equal(["dotnet build"], runner.Commands);
  }

  [Fact]
  public async Task CheckGoal_FailingCommand_ReportsViolated()
  {
    FakeStateStore store = new();
    StateService service = new(store, new ForbiddenEvidenceRunner(), new StubWorkspace(),
        goalRunner: new FakeGoalRunner(c => new EvidenceResult(c, false, "exit code 1")));
    _ = await service.SetAsync("goal/check", "[\"dotnet build\"]", null,
        TestContext.Current.CancellationToken);

    CertificationReport report = await service.CheckGoalAsync(TestContext.Current.CancellationToken);

    Assert.False(report.Certified);
    Assert.Contains("exit code 1", report.BlockingReasons.Single(), StringComparison.Ordinal);
  }

  // The run must leave a freshness-bearing outcome behind, or the gate can never be
  // satisfied no matter how many times it passes.
  [Fact]
  public async Task CheckGoal_RecordsOutcomeWithWorkspaceDigest()
  {
    WorkspaceSnapshot snapshot = Snapshot();
    (StateService service, FakeStateStore store, _) = await CreateAsync(snapshot);

    _ = await service.CheckGoalAsync(TestContext.Current.CancellationToken);

    Assert.True(store.TryGetValue("goal/lastcheck", out string? stored));
    Assert.Contains(snapshot.Digest, stored, StringComparison.Ordinal);
    Assert.Contains("\"passed\":true", stored, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CheckGoal_NoCommandsStored_ReportsSo()
  {
    StateService service = new(new FakeStateStore(), new ForbiddenEvidenceRunner(),
        new StubWorkspace(),
        goalRunner: new FakeGoalRunner(c => new EvidenceResult(c, true, string.Empty)));

    CertificationReport report = await service.CheckGoalAsync(TestContext.Current.CancellationToken);

    Assert.False(report.Certified);
    Assert.Contains("No goal/check", report.BlockingReasons.Single(), StringComparison.Ordinal);
  }

  [Fact]
  public async Task CheckGoal_NoRunnerWired_ReportsSo()
  {
    FakeStateStore store = new();
    StateService service = new(store, new ForbiddenEvidenceRunner(), new StubWorkspace());
    _ = await service.SetAsync("goal/check", "[\"dotnet build\"]", null,
        TestContext.Current.CancellationToken);

    CertificationReport report = await service.CheckGoalAsync(TestContext.Current.CancellationToken);

    Assert.False(report.Certified);
    Assert.Contains("No goal command runner", report.BlockingReasons.Single(), StringComparison.Ordinal);
  }
}
