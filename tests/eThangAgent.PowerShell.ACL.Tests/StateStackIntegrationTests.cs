using System.Collections;
using eThangAgent.CapabilityDomain;
using eThangAgent.PowerShell.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.PowerShell.ACL.Tests;

/// <summary>Composes the real stack exactly as the CLI does — registry with both
///     providers over real SQLite and the real evidence runner — and walks the
///     discipline loop through the broker. Isolates hangs from the child-process
///     E2E layer.</summary>
public class StateStackIntegrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ethang-stack-{Guid.NewGuid():N}.db");
    private readonly StubWorkspace _workspace = new();
    private readonly CapabilityRegistry _registry;

    public StateStackIntegrationTests()
    {
        var database = new AppDatabase(_dbPath);
        var store = new SqliteStateStore(database);
        var runner = new PsEvidenceRunner(EvidenceOptions.Default);
        var service = new StateService(store, runner, _workspace, EvidenceOptions.Default);
        var readTool = new ReadTool(new FakeFileSystemAccess());
        var stateProvider = new StateCapabilityProvider(service);
        var agentProvider = new AgentToolsProvider("agent",
            [new AgentToolBinding(readTool, "Read lines.")]);
        _registry = CapabilityRegistry.Create([agentProvider, stateProvider]);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task Set_And_Transition_WithoutVerify_Complete()
    {
        var broker = new ToolBroker(_registry);

        broker.InvokeTool("state.set", new Hashtable { ["key"] = "current/head", ["value"] = "done" });
        broker.InvokeTool("state.transition", new Hashtable
        {
            ["from"] = "coding",
            ["to"] = "done",
            ["summary"] = "work",
            ["evidence"] = new ArrayList { "Write-Output evidence-ok" },
        });
    }

    [Fact]
    public async Task FullDisciplineLoop_Certifies()
    {
        var broker = new ToolBroker(_registry);

        broker.InvokeTool("state.set", new Hashtable { ["key"] = "current/head", ["value"] = "done" });
        broker.InvokeTool("state.transition", new Hashtable
        {
            ["from"] = "coding",
            ["to"] = "done",
            ["summary"] = "work",
            ["evidence"] = new ArrayList { "Write-Output evidence-ok" },
        });
        var report = broker.InvokeTool("state.verify", new Hashtable());

        Assert.Contains("\"Certified\":true", report);
    }

    [Fact]
    public async Task OuterEngine_WithNestedStateVerify_Completes()
    {
        var engine = new PowerShellExecEngine(
            new Lazy<ICapabilityRegistry>(() => _registry),
            ExecOptions.Default);
        var run = await engine.ExecuteAsync(new ExecProgram(
            "state.set @{ key = 'current/head'; value = 'done' }\n" +
            "state.transition @{ from = 'coding'; to = 'done'; summary = 'work'; " +
                "evidence = @('Write-Output evidence-ok') }\n" +
            "state.verify @{}"));

        Assert.True(run.Status == ExecRunStatus.Completed,
            $"status={run.Status}; errors={string.Join(" | ", run.ErrorLines)}; msg={run.ErrorMessage}");
        Assert.Contains("Certified\":true", run.Output);
    }

    private sealed class StubWorkspace : IWorkspaceContext
    {
        public string WorkspaceId => "ws-stack";
    }

    private sealed class FakeFileSystemAccess : IFileSystemAccess
    {
        public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine,
            CancellationToken ct = default)
            => Task.FromResult(Result<FileRead>.Success(new FileRead(["alpha"], 1, 1)));
    }
}
