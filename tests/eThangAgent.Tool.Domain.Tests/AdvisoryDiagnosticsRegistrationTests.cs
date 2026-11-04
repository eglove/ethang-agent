using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain.Lsp;

namespace eThangAgent.ToolDomain.Tests;

// Issue #356: advisory diagnostics ride the existing tool bindings, so children
// inherit them via AnchoredToolRegistry. RootedAt carries the source and ledger;
// the registry re-roots the edit tool at the anchor.
public class AdvisoryDiagnosticsRegistrationTests
{
  private sealed class ScriptedSource : IAdvisoryDiagnosticsSource
  {
    public List<string> Paths { get; } = [];
    public Task<AdvisoryDiagnosticsResult> GetAsync(string path, CancellationToken ct = default)
    {
      Paths.Add(path);
      return Task.FromResult(new AdvisoryDiagnosticsResult(AdvisoryDiagnosticsState.Ready, []));
    }
  }

  private sealed class FakeEditAccess : IFileEditAccess
  {
    public Task<Result<ReplaceOutcome>> ReplaceInFileAsync(
        string path, string oldText, string newText, int? occurrences, CancellationToken ct = default)
      => Task.FromResult(Result.Success(new ReplaceOutcome(1, 3, LineEnding.Lf, [2])));
    public Task<Result<ReplaceOutcome>> ReplaceLineRangeAsync(
        string path, int startLine, int endLine, string newText, CancellationToken ct = default)
      => Task.FromResult(Result.Success(new ReplaceOutcome(1, 3, LineEnding.Lf, [2])));
    public Task<Result<HunkReplaceOutcome>> ReplaceHunksAsync(
        string path, IReadOnlyList<EditHunk> hunks, CancellationToken ct = default)
      => Task.FromResult(Result.Success(new HunkReplaceOutcome([1], 3, LineEnding.Lf, [2])));
  }

  private sealed class SingleToolRegistry(ITool tool) : IToolRegistry
  {
    public ITool? Find(string name) => tool;
    public IReadOnlyList<ToolDefinition> Definitions => [tool.Definition];
  }

  [Fact]
  public async Task AnchoredChild_InheritsAdvisoryDiagnostics()
  {
    ScriptedSource source = new();
    SessionAdvisoryDiagnosticsLedger ledger = new();
    EditTool root = new(new WorkspacePathResolver(@"C:\ws"), new FakeEditAccess(), source, ledger);
    AnchoredToolRegistry anchored = new(new SingleToolRegistry(root), @"C:\ws\child");
    EditTool child = Assert.IsType<EditTool>(anchored.Find("edit"));
    ToolResult result = await child.ExecuteAsync(new RawToolInput("edit",
        "{\"timeoutSeconds\":120,\"path\":\"a.cs\",\"old\":\"x\",\"replacement\":\"y\",\"occurrences\":1}"), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal([@"C:\ws\child\a.cs"], source.Paths);
    AdvisoryDiagnosticsEntry entry = Assert.Single(ledger.Snapshot());
    Assert.Equal(@"C:\ws\child\a.cs", entry.Path);
  }

  [Fact]
  public void RootedAt_CarriesSourceAndLedger()
  {
    ScriptedSource source = new();
    SessionAdvisoryDiagnosticsLedger ledger = new();
    EditTool root = new(new WorkspacePathResolver(@"C:\ws"), new FakeEditAccess(), source, ledger);
    EditTool rooted = Assert.IsType<EditTool>(root.RootedAt(@"C:\ws\child"));
    Assert.Equal(root.Definition.Name, rooted.Definition.Name);
    Assert.Equal(root.Definition.Description, rooted.Definition.Description);
  }
}
