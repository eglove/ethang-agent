using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.StateDomain.Tests;

/// <summary>Issue #317 D5/D6, found by #304: a seam that takes a PATH must be given the
///     workspace ROOT, never the stable identity.
///     <para>THE DEFECT THIS PINS was measured in the #304 session: every workspace opened after
///     #317 has a minted identity (`ws-bb017210...`), and `checkgoal` passed THAT to the shell
///     runner's working-directory parameter. The shell could not start ("The directory name is
///     invalid"), so the completion gate was UNRUNNABLE in this workspace while reporting
///     `Violated: true` - a gate that could never pass, which is indistinguishable from a gate
///     nobody configured.</para>
///     <para>IT COMPILED because both facts were once the same string. The split exists to make
///     the mistake visible, and this test is what makes it visible at the seam.</para></summary>
public class CheckGoalWorkspaceRootTests
{
  private const string Identity = "ws-bb017210a3ef40538f1b8c2e879090e6";
  private const string Root = "C:\\ws\\real-directory";

  /// <summary>The post-#317 shape: a minted identity that is NOT a directory, and the root it
  ///     is bound to.</summary>
  private sealed class SplitWorkspace : IWorkspaceContext
  {
    public string WorkspaceId => Identity;

    public string WorkspaceRoot => Root;
  }

  private sealed class ForbiddenEvidenceRunner : IEvidenceRunner
  {
    public Task<EvidenceResult> RunAsync(string command, CancellationToken ct = default) =>
        throw new InvalidOperationException("goal/check must never reach the C# evidence runner");
  }

  private sealed class RecordingGoalRunner : IGoalCommandRunner
  {
    public List<string> WorkingDirectories { get; } = [];

    public Task<EvidenceResult> RunAsync(string workingDirectory, string command,
        CancellationToken ct = default)
    {
      WorkingDirectories.Add(workingDirectory);
      return Task.FromResult(new EvidenceResult(command, true, string.Empty));
    }
  }

  private sealed class RecordingSnapshot : IWorkspaceSnapshotAccess
  {
    public List<string> Roots { get; } = [];

    public Task<WorkspaceSnapshot> CaptureAsync(string workspaceRoot, CancellationToken ct = default)
    {
      Roots.Add(workspaceRoot);
      return Task.FromResult(WorkspaceSnapshot.Create(workspaceRoot,
          new Dictionary<string, WorkspaceFileFingerprint>(StringComparer.OrdinalIgnoreCase)
          {
            ["a.cs"] = new(1, DateTime.UtcNow, 1),
          }));
    }
  }

  private static async Task<(StateService Service, RecordingGoalRunner Runner, RecordingSnapshot Snapshot)>
      ServiceAsync()
  {
    RecordingGoalRunner runner = new();
    RecordingSnapshot snapshots = new();
    StateService service = new(new FakeStore(), new ForbiddenEvidenceRunner(),
        new SplitWorkspace(), changes: null, goalRunner: runner, snapshots: snapshots);
    _ = await service.SetAsync("goal/check", "[\"dotnet build\"]", null,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    return (service, runner, snapshots);
  }

  // THE DEFECT: the runner was handed the identity, so the shell could not start.
  [Fact]
  public async Task CheckGoal_RunsTheCommandsInTheWorkspaceRoot_NotTheIdentity()
  {
    (StateService service, RecordingGoalRunner runner, _) = await ServiceAsync();

    _ = await service.CheckGoalAsync(TestContext.Current.CancellationToken);

    Assert.Equal([Root], runner.WorkingDirectories);
  }

  // The digest half had the same defect: a snapshot of a minted identity is Unavailable, which
  // the gate reads as 'unverifiable' forever - a gate that could never be fresh.
  [Fact]
  public async Task CheckGoal_DigestsTheWorkspaceRoot_NotTheIdentity()
  {
    (StateService service, _, RecordingSnapshot snapshots) = await ServiceAsync();

    _ = await service.CheckGoalAsync(TestContext.Current.CancellationToken);

    Assert.Equal([Root], snapshots.Roots);
  }

  /// <summary>The smallest state store the checkgoal path needs: keys and nothing else.</summary>
  private sealed class FakeStore : IStateStore
  {
    private readonly Dictionary<string, StateKeyValue> _keys = new(StringComparer.Ordinal);

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
}
