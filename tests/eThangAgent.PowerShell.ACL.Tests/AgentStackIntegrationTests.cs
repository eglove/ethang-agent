using System.Text.Json;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.AgentInfrastructure;
using eThangAgent.CapabilityDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.PowerShell.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.PowerShell.ACL.Tests;

/// <summary>Composes the full agent stack exactly as the design wires it — real runspace engine
///     over the capability registry, StartSpawnHandler behind the agent capability provider,
///     InProcessAgentRuntime driving the real SubAgentSpawner child loop, real SqliteAgentStore
///     on a temp app database — with an in-proc scripted model provider standing in for
///     OpenRouter. Children reach agent.spawn the same way the root does: an exec tool call
///     whose program invokes the capability. Proves async nested spawn end-to-end: spawn returns
///     a running line without the report, children complete in the background with persisted
///     transcripts, grandchild nesting with ParentId chain, depth-limit rejection arriving as a
///     well-formed tool result, and dual-name wrappers.</summary>
public class AgentStackIntegrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ethang-agent-stack-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task Spawn_ThroughRealEngine_ChildCompletes_PersistsRowAndTranscript()
    {
        var (engine, store, factory, root) = ComposeStack(
            new SubAgentOptions(DefaultModel: "stack/child-model"));
        factory.Script("stack/child-model",
            CallsExec("call_1", "Write-Output 'probe-ok'"),
            FinalReport("child report body"));

        var run = await engine.ExecuteAsync(new ExecProgram(
            "agent.spawn @{ taskPrompt = 'summarize'; label = 'child-a' }"));

        Assert.True(run.Status == ExecRunStatus.Completed,
            $"status={run.Status}; errors={string.Join(" | ", run.ErrorLines)}; msg={run.ErrorMessage}");
        // Non-blocking contract: the spawn action returns the running line only — the report
        // arrives later through the persisted record, not through the spawn result.
        Assert.Matches("id=[0-9a-fA-F-]{36} status=running", run.Output);
        Assert.DoesNotContain("child report body", run.Output);
        Assert.DoesNotContain("--- report ---", run.Output);

        var child = await AwaitTerminalAsync(store, root.Id);
        Assert.Equal(AgentStatus.Completed, child.Status);
        Assert.Null(child.FailureReason);
        Assert.Equal(1, child.Depth);
        Assert.Equal(root.Id, child.ParentId);
        Assert.Equal("child-a", child.Label);
        Assert.Equal("summarize", child.TaskPrompt);
        Assert.Equal("child report body", child.FinalReport);

        var transcript = (await store.GetTranscriptAsync(child.Id)).Value!;
        Assert.Equal(4, transcript.Count);
        Assert.Equal(Role.User, transcript[0].Role);
        Assert.Equal("summarize", transcript[0].Content);
        Assert.Equal(Role.Assistant, transcript[1].Role);
        Assert.NotNull(transcript[1].ToolCalls);
        Assert.Equal(Role.Tool, transcript[2].Role);
        Assert.Contains("probe-ok", transcript[2].Content);
        Assert.Equal(Role.Assistant, transcript[3].Role);
        Assert.Equal("child report body", transcript[3].Content);
    }

    [Fact]
    public async Task Spawn_NestsGrandchild_AtDepth2_WithParentChainPersisted()
    {
        var (engine, store, factory, root) = ComposeStack(
            new SubAgentOptions(DefaultModel: "stack/child-model"));
        factory.Script("stack/child-model",
            CallsExec("call_1",
                "agent.spawn @{ taskPrompt = 'grandchild task'; " +
                    "model = 'stack/grandchild-model'; label = 'grandchild-b' }"),
            FinalReport("child wrapped the grandchild"));
        factory.Script("stack/grandchild-model",
            CallsExec("gc_1", "Write-Output 'grandchild-probe'"),
            FinalReport("grandchild report body"));

        var run = await engine.ExecuteAsync(new ExecProgram(
            "agent.spawn @{ taskPrompt = 'delegate nesting'; label = 'child-b' }"));

        Assert.True(run.Status == ExecRunStatus.Completed,
            $"status={run.Status}; errors={string.Join(" | ", run.ErrorLines)}; msg={run.ErrorMessage}");
        Assert.Contains("status=running", run.Output);

        var child = await AwaitTerminalAsync(store, root.Id);
        Assert.Equal(AgentStatus.Completed, child.Status);
        Assert.Equal(1, child.Depth);
        Assert.Equal(root.Id, child.ParentId);

        var grandchild = await AwaitTerminalAsync(store, child.Id);
        Assert.Equal(AgentStatus.Completed, grandchild.Status);
        Assert.Equal(2, grandchild.Depth);
        Assert.Equal(child.Id, grandchild.ParentId);
        Assert.Equal("grandchild-b", grandchild.Label);
        Assert.Equal("grandchild task", grandchild.TaskPrompt);
        Assert.Equal("grandchild report body", grandchild.FinalReport);

        var transcript = (await store.GetTranscriptAsync(grandchild.Id)).Value!;
        Assert.True(transcript.Count >= 2);
        Assert.Equal(Role.User, transcript[0].Role);
        Assert.Equal("grandchild task", transcript[0].Content);
    }

    [Fact]
    public async Task Spawn_AtDepthLimit_CanonicalErrorAsWellFormedToolResult_NoDepth3Row()
    {
        // MaxDepth 2 places the guard at the shallowest scriptable depth so the rejection
        // surfaces two levels down through the real engine; the depth boundary itself is
        // unit-covered by StartSpawnHandlerTests. The fact asserts the guard arrives as a
        // well-formed tool result: the spawning grandchild's run keeps going and completes,
        // and nothing persists for the rejection.
        var (engine, store, factory, root) = ComposeStack(
            new SubAgentOptions(DefaultModel: "stack/child-model", MaxDepth: 2));
        factory.Script("stack/child-model",
            CallsExec("call_1",
                "agent.spawn @{ taskPrompt = 'grandchild task'; model = 'stack/grandchild-model' }"),
            FinalReport("child finished despite the nested rejection"));
        factory.Script("stack/grandchild-model",
            CallsExec("gc_1", "agent.spawn @{ taskPrompt = 'one level too deep' }"),
            FinalReport("grandchild survived the rejection"));

        var run = await engine.ExecuteAsync(new ExecProgram(
            "agent.spawn @{ taskPrompt = 'reject probe'; label = 'child-c' }"));

        Assert.True(run.Status == ExecRunStatus.Completed,
            $"status={run.Status}; errors={string.Join(" | ", run.ErrorLines)}; msg={run.ErrorMessage}");

        var child = await AwaitTerminalAsync(store, root.Id);
        Assert.Equal(AgentStatus.Completed, child.Status);
        Assert.Equal(1, child.Depth);

        var grandchild = await AwaitTerminalAsync(store, child.Id);
        Assert.Equal(AgentStatus.Completed, grandchild.Status);
        Assert.Null(grandchild.FailureReason);
        Assert.Equal(2, grandchild.Depth);

        // The rejected spawn reached the grandchild's model as a well-formed tool result
        // carrying the canonical error line, not as a crash.
        var transcript = (await store.GetTranscriptAsync(grandchild.Id)).Value!;
        Assert.Contains(transcript, m =>
            m.Role == Role.Tool && m.Content.Contains("Error [DepthExceeded]"));
        Assert.Contains(transcript, m =>
            m.Role == Role.Assistant && m.Content.Contains("grandchild survived the rejection"));

        // Nothing was persisted for the rejected spawn: no child of the grandchild.
        Assert.Empty((await store.ListChildrenAsync(grandchild.Id)).Value!);
    }

    [Fact]
    public async Task Spawn_BareName_WrapperStartsChild()
    {
        var (engine, store, factory, root) = ComposeStack(
            new SubAgentOptions(DefaultModel: "stack/child-model"));
        factory.Script("stack/child-model", FinalReport("bare spawn report"));

        var run = await engine.ExecuteAsync(new ExecProgram(
            "spawn @{ taskPrompt = 'bare invocation'; label = 'bare-a' }"));

        Assert.True(run.Status == ExecRunStatus.Completed,
            $"status={run.Status}; errors={string.Join(" | ", run.ErrorLines)}; msg={run.ErrorMessage}");
        Assert.Matches("id=[0-9a-fA-F-]{36} status=running", run.Output);

        var child = await AwaitTerminalAsync(store, root.Id);
        Assert.Equal(AgentStatus.Completed, child.Status);
        Assert.Equal("bare invocation", child.TaskPrompt);
        Assert.Equal("bare spawn report", child.FinalReport);
    }

    /// <summary>Wires the stack with the composition-root shape: lazy registry into the
    ///     engine, exec as the agents' only tool, the spawn command and queries (CQRS split)
    ///     behind the agent capability provider resolving the ambient running child (falling
    ///     back to the unpersisted root record at depth 0) as the spawn parent, and the
    ///     runtime driving the child loop on background tasks.</summary>
    private (PowerShellExecEngine Engine, SqliteAgentStore Store,
        ScriptedProviderFactory Factory, AgentRecord Root) ComposeStack(SubAgentOptions options)
    {
        var database = new AppDatabase(_dbPath);
        var store = new SqliteAgentStore(database);
        var factory = new ScriptedProviderFactory();
        var rootRecord = AgentRecord.Spawned(AgentId.NewId(), null, 0, "stack/root-model",
            null, "root task", DateTimeOffset.UtcNow);

        ICapabilityRegistry registry = null!;
        var engine = new PowerShellExecEngine(
            new Lazy<ICapabilityRegistry>(() => registry), ExecOptions.Default);
        var execTool = new ExecTool(engine, ExecOptions.Default,
            new TempArtifactStore(), NullExecActivitySink.Instance);
        var spawner = new SubAgentSpawner(factory, store, new ToolRegistry([execTool]),
            new StaticPromptProvider("stack guide"), options);
        var runtime = new InProcessAgentRuntime(spawner, store, maxConcurrentAgents: 4);
        var spawnCommand = new StartSpawnHandler(store, runtime, options);
        var agentProvider = new AgentCapabilityProvider(spawnCommand,
            new AgentQueries(store), () => SubAgentSpawner.RunningChild ?? rootRecord);
        registry = CapabilityRegistry.Create([agentProvider]);

        return (engine, store, factory, rootRecord);
    }

    /// <summary>Children run on background tasks once spawned; integration assertions poll the
    ///     store until the record reaches a terminal state instead of sleeping blindly.</summary>
    private static async Task<AgentRecord> AwaitTerminalAsync(SqliteAgentStore store, AgentId parentId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var children = (await store.ListChildrenAsync(parentId)).Value!;
            var terminal = children.FirstOrDefault(c =>
                c.Status is AgentStatus.Completed or AgentStatus.Failed);
            if (terminal is not null)
                return terminal;
            await Task.Delay(20);
        }
        throw new TimeoutException($"no child of {parentId} reached a terminal state within 10s.");
    }

    private static Result<ModelResponse> FinalReport(string text)
        => Result<ModelResponse>.Success(new ModelResponse(text, []));

    private static Result<ModelResponse> CallsExec(string callId, string program)
        => Result<ModelResponse>.Success(new ModelResponse(null,
            [new ToolCallRequest(callId, "exec",
                "{\"program\":" + JsonSerializer.Serialize(program) + "}")]));

    /// <summary>In-proc scripted provider: replays its queue, then answers with a final
    ///     report so an unexpected extra turn can never hang the run.</summary>
    private sealed class ScriptedProvider(Queue<Result<ModelResponse>> script) : IModelProvider
    {
        public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
            CancellationToken ct = default)
            => Task.FromResult(script.Count > 0
                ? script.Dequeue()
                : Result<ModelResponse>.Success(new ModelResponse("unscripted final report", [])));
    }

    /// <summary>Hands each spawn its own provider scripted per model id, so child and
    ///     grandchild levels replay independent conversations.</summary>
    private sealed class ScriptedProviderFactory : IModelProviderFactory
    {
        private readonly Dictionary<string, Queue<Result<ModelResponse>>> _scripts =
            new(StringComparer.Ordinal);

        public void Script(string modelId, params Result<ModelResponse>[] responses)
            => _scripts[modelId] = new Queue<Result<ModelResponse>>(responses);

        public IModelProvider Create(ModelConfig config)
            => new ScriptedProvider(_scripts.TryGetValue(config.ModelId, out var script)
                ? script
                : new Queue<Result<ModelResponse>>());
    }

    private sealed class TempArtifactStore : IExecOutputStore
    {
        public Task<string> WriteAsync(string content, CancellationToken ct = default)
        {
            var path = Path.Combine(Path.GetTempPath(), $"ethang-artifact-{Guid.NewGuid():N}.txt");
            File.WriteAllText(path, content);
            return Task.FromResult(path);
        }
    }
}
