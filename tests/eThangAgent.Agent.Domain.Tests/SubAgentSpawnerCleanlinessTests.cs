using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>The child-report contract's workspace-cleanliness check (grand-plan
///     BUG 3): a completed run's report gains an annotation listing untracked files
///     left behind in the child's anchored workspace. Best effort: a failing check,
///     no check wired, no anchor, or an empty untracked list leaves the report
///     verbatim — the run still completes.</summary>
public class SubAgentSpawnerCleanlinessTests
{
  private static AgentRecord Child(string? anchor)
      => AgentRecord.Spawned(AgentId.NewId(), null, 1, "m/sub", null, "do things",
          new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc),
          anchor is null ? null : new SpawnContract(WorkspaceRoot: anchor));

  private static SubAgentSpawner MakeRunner(IModelProvider provider, FakeAgentStore store,
      IWorkspaceCleanlinessCheck? cleanliness)
  {
    SubAgentServices services = new(
        new FakeModelProviderFactory(provider), store, new ToolRegistry([]),
        new StaticPromptProvider("guide"), new SubAgentOptions(DefaultModel: "m/sub"),
        AnchorScope: new RecordingScope(),
        Cleanliness: cleanliness);
    return new SubAgentSpawner(services);
  }

  /// <summary>The anchored contract requires a wired anchor scope (an infrastructure
  ///     fault otherwise); the cleanliness tests never dispatch tools, so a plain
  ///     holder suffices.</summary>
  private sealed class RecordingScope : IWorkspaceAnchorScope
  {
    public string? Current { get; set; }
  }

  private sealed class FakeCleanliness(IReadOnlyList<string> files, bool fail = false)
      : IWorkspaceCleanlinessCheck
  {
    public int Calls { get; private set; }

    public Task<Result<IReadOnlyList<string>>> UntrackedFilesAsync(string workspaceRoot,
        CancellationToken ct = default)
    {
      Calls++;
      return Task.FromResult(fail
          ? Result.Failure<IReadOnlyList<string>>(new DomainError("NotAWorkTree", "no git here"))
          : Result.Success(files));
    }
  }

  [Fact]
  public async Task RunAsync_UntrackedFilesPresent_AnnotationAppendedToReport()
  {
    FakeAgentStore store = new();
    FakeProvider provider = new(Result.Success(new ModelResponse("child report", [])));
    FakeCleanliness check = new(["stage_1.txt", "notes.md"]);
    SubAgentSpawner spawner = MakeRunner(provider, store, check);

    AgentRunOutcome outcome = await spawner.RunAsync(Child(@"C:\ws"), CancellationToken.None);

    Assert.Equal(AgentStatus.Completed, outcome.Status);
    Assert.Equal(1, check.Calls);
    Assert.StartsWith("child report", outcome.Report, StringComparison.Ordinal);
    Assert.Contains("[agent] workspace check: 2 untracked file(s) left behind: stage_1.txt, notes.md",
        outcome.Report, StringComparison.Ordinal);
    AgentRecord updated = Assert.Single(store.Updated);
    Assert.Contains("[agent] workspace check", updated.FinalReport!, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RunAsync_NoUntrackedFiles_ReportUnchanged()
  {
    FakeAgentStore store = new();
    FakeProvider provider = new(Result.Success(new ModelResponse("child report", [])));
    FakeCleanliness check = new([]);
    SubAgentSpawner spawner = MakeRunner(provider, store, check);

    AgentRunOutcome outcome = await spawner.RunAsync(Child(@"C:\ws"), CancellationToken.None);

    Assert.Equal("child report", outcome.Report);
  }

  [Fact]
  public async Task RunAsync_CheckFails_ReportUnchangedRunCompletes()
  {
    FakeAgentStore store = new();
    FakeProvider provider = new(Result.Success(new ModelResponse("child report", [])));
    FakeCleanliness check = new([], fail: true);
    SubAgentSpawner spawner = MakeRunner(provider, store, check);

    AgentRunOutcome outcome = await spawner.RunAsync(Child(@"C:\ws"), CancellationToken.None);

    Assert.Equal(AgentStatus.Completed, outcome.Status);
    Assert.Equal("child report", outcome.Report);
  }

  [Fact]
  public async Task RunAsync_NoCheckWired_ReportUnchanged()
  {
    FakeAgentStore store = new();
    FakeProvider provider = new(Result.Success(new ModelResponse("child report", [])));
    SubAgentSpawner spawner = MakeRunner(provider, store, null);

    AgentRunOutcome outcome = await spawner.RunAsync(Child(@"C:\ws"), CancellationToken.None);

    Assert.Equal("child report", outcome.Report);
  }

  [Fact]
  public async Task RunAsync_UnanchoredContract_CheckNeverCalled()
  {
    FakeAgentStore store = new();
    FakeProvider provider = new(Result.Success(new ModelResponse("child report", [])));
    FakeCleanliness check = new(["stage_1.txt"]);
    SubAgentSpawner spawner = MakeRunner(provider, store, check);

    _ = await spawner.RunAsync(Child(null), CancellationToken.None);

    Assert.Equal(0, check.Calls);
  }

  [Fact]
  public async Task RunAsync_ManyUntrackedFiles_ListCappedAtTenWithOverflow()
  {
    FakeAgentStore store = new();
    FakeProvider provider = new(Result.Success(new ModelResponse("child report", [])));
    List<string> files = [.. Enumerable.Range(1, 13).Select(i => $"scratch_{i}.txt")];
    FakeCleanliness check = new(files);
    SubAgentSpawner spawner = MakeRunner(provider, store, check);

    AgentRunOutcome outcome = await spawner.RunAsync(Child(@"C:\ws"), CancellationToken.None);

    Assert.Contains("scratch_10.txt", outcome.Report, StringComparison.Ordinal);
    Assert.DoesNotContain("scratch_11.txt", outcome.Report, StringComparison.Ordinal);
    Assert.Contains("(+3 more)", outcome.Report, StringComparison.Ordinal);
    Assert.Contains("13 untracked file(s)", outcome.Report, StringComparison.Ordinal);
  }
}
