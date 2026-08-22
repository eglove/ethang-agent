using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Coverage of the three capability actions against fake command/queries fakes:
///     spawn renders only the running line (non-blocking), status renders the state line
///     with a reason suffix when failed, result passes the query outcome through verbatim,
///     ids parse strictly as Guid "D", unknown actions get typed errors.</summary>
public class AgentCapabilityProviderTests
{
    private static AgentRecord ParentAtDepth(int depth) =>
        AgentRecord.Spawned(AgentId.NewId(), null, depth, "prov/parent", null, "root task", DateTimeOffset.UtcNow);

    private static AgentRecord ChildIn(AgentStatus status, AgentFailureReason? reason = null,
        string? report = null)
        => AgentRecord.Spawned(AgentId.NewId(), ParentAtDepth(0).Id, 1, "prov/child", null,
                "child task", DateTimeOffset.UtcNow) with
        {
            Status = status,
            FailureReason = reason,
            CompletedAt = status is AgentStatus.Running ? null : DateTimeOffset.UtcNow,
            FinalReport = report,
        };

    private sealed class FakeSpawnCommand(Result<AgentId> reply,
        List<(AgentRecord Parent, SpawnRequest Request)> calls) : IAgentSpawnCommand
    {
        public Task<Result<AgentId>> Execute(AgentRecord parent, SpawnRequest request, CancellationToken ct = default)
        {
            calls.Add((parent, request));
            return Task.FromResult(reply);
        }
    }

    private sealed class FakeQueries : IAgentQueries
    {
        public readonly Dictionary<AgentId, Result<AgentRecord>> Statuses = new();
        public readonly Dictionary<AgentId, Result<string>> Results = new();
        public readonly List<AgentId> StatusCalls = [];
        public readonly List<AgentId> ResultCalls = [];

        public Task<Result<AgentRecord>> GetStatus(AgentId id, CancellationToken ct = default)
        {
            StatusCalls.Add(id);
            return Task.FromResult(Statuses.TryGetValue(id, out var result)
                ? result
                : Result<AgentRecord>.Failure(new Error("NotFound", $"No agent exists with id '{id}'.")));
        }

        public Task<Result<string>> GetResult(AgentId id, CancellationToken ct = default)
        {
            ResultCalls.Add(id);
            return Task.FromResult(Results.TryGetValue(id, out var result)
                ? result
                : Result<string>.Failure(new Error("NotFound", $"No agent exists with id '{id}'.")));
        }
    }

    private static (AgentCapabilityProvider Provider, FakeSpawnCommand Command, FakeQueries Queries,
        List<(AgentRecord Parent, SpawnRequest Request)> Calls)
        MakeProvider(AgentRecord parent,
            Result<AgentId>? spawnReply = null,
            Action<FakeQueries>? seed = null)
    {
        var calls = new List<(AgentRecord, SpawnRequest)>();
        var command = new FakeSpawnCommand(spawnReply ?? Result<AgentId>.Success(AgentId.NewId()), calls);
        var queries = new FakeQueries();
        seed?.Invoke(queries);
        return (new AgentCapabilityProvider(command, queries, () => parent), command, queries, calls);
    }

    // --- spawn ---------------------------------------------------------------

    [Fact]
    public async Task Spawn_Success_RendersRunningLine_WithoutAnyReport()
    {
        var id = AgentId.NewId();
        var parent = ParentAtDepth(0);
        var (provider, _, _, calls) = MakeProvider(parent, Result<AgentId>.Success(id));

        var result = await provider.InvokeAsync("spawn",
            """{"taskPrompt":"summarize","model":"prov/model-x","label":"research"}""");

        Assert.False(result.IsError);
        // Non-blocking contract: exactly the running line — no report gutter, no label segment.
        Assert.Equal($"id={id} status=running", result.Content);
        Assert.DoesNotContain("--- report ---", result.Content);

        var call = Assert.Single(calls);
        Assert.Equal(parent, call.Parent);
        Assert.Equal("summarize", call.Request.TaskPrompt);
        Assert.Equal("prov/model-x", call.Request.Model);
        Assert.Equal("research", call.Request.Label);
    }

    [Fact]
    public async Task Spawn_HandlerFailure_PassesCanonicalErrorThroughUntouched()
    {
        var (provider, _, _, _) = MakeProvider(ParentAtDepth(2),
            Result<AgentId>.Failure(new Error("DepthExceeded",
                "agent depth 2 is at the limit (3); children cannot spawn further")));

        var result = await provider.InvokeAsync("spawn", """{"taskPrompt":"x"}""");

        Assert.True(result.IsError);
        Assert.Equal("Error [DepthExceeded]: agent depth 2 is at the limit (3); children cannot spawn further",
            result.Content);
    }

    [Fact]
    public async Task Spawn_CapReached_CanonicalStringRoundTripsByteForByte()
    {
        // The runtime builds its cap Error by parsing RuntimeErrors.CapReached; rendering must
        // reproduce that canonical string exactly so callers can match on it.
        var canonical = RuntimeErrors.CapReached;
        var codeStart = canonical.IndexOf('[') + 1;
        var codeEnd = canonical.IndexOf(']', codeStart);
        var error = new Error(canonical[codeStart..codeEnd], canonical[(codeEnd + 3)..]);
        var (provider, _, _, _) = MakeProvider(ParentAtDepth(0), Result<AgentId>.Failure(error));

        var result = await provider.InvokeAsync("spawn", """{"taskPrompt":"x"}""");

        Assert.True(result.IsError);
        Assert.Equal(canonical, result.Content);
    }

    [Theory]
    [InlineData("""{"taskPrompt":"x","bogus":1}""", "bogus")]
    [InlineData("""{}""", "taskPrompt")]
    [InlineData("""{"taskPrompt":"  "}""", "taskPrompt")]
    [InlineData("""{"taskPrompt":"x","model":" "}""", "model")]
    public async Task Spawn_InvalidInput_TypedErrorNamingField(string json, string expected)
    {
        var (provider, _, _, calls) = MakeProvider(ParentAtDepth(0));

        var result = await provider.InvokeAsync("spawn", json);

        Assert.True(result.IsError);
        Assert.Contains(expected, result.Content);
        Assert.Empty(calls);
    }

    // --- status --------------------------------------------------------------

    [Fact]
    public async Task Status_Running_RendersBareRunningLine()
    {
        var child = ChildIn(AgentStatus.Running);
        var (provider, _, queries, _) = MakeProvider(ParentAtDepth(0), seed: q => q.Statuses[child.Id] =
            Result<AgentRecord>.Success(child));

        var result = await provider.InvokeAsync("status", $$"""{"id":"{{child.Id}}"}""");

        Assert.False(result.IsError);
        Assert.Equal($"id={child.Id} status=running", result.Content);
        Assert.Equal(child.Id, Assert.Single(queries.StatusCalls));
    }

    [Fact]
    public async Task Status_Completed_RendersCompletedWithoutReasonSuffix()
    {
        var child = ChildIn(AgentStatus.Completed, report: "the final report");
        var (provider, _, _, _) = MakeProvider(ParentAtDepth(0), seed: q => q.Statuses[child.Id] =
            Result<AgentRecord>.Success(child));

        var result = await provider.InvokeAsync("status", $$"""{"id":"{{child.Id}}"}""");

        Assert.False(result.IsError);
        Assert.Equal($"id={child.Id} status=completed", result.Content);
    }

    [Theory]
    [InlineData(AgentFailureReason.MaxIterations, "max-iterations")]
    [InlineData(AgentFailureReason.Timeout, "timeout")]
    [InlineData(AgentFailureReason.ProviderError, "provider-error")]
    public async Task Status_Failed_RendersStateLineWithReasonSuffix(AgentFailureReason reason, string text)
    {
        var child = ChildIn(AgentStatus.Failed, reason);
        var (provider, _, _, _) = MakeProvider(ParentAtDepth(0), seed: q => q.Statuses[child.Id] =
            Result<AgentRecord>.Success(child));

        var result = await provider.InvokeAsync("status", $$"""{"id":"{{child.Id}}"}""");

        Assert.False(result.IsError);
        Assert.Equal($"id={child.Id} status=failed reason={text}", result.Content);
    }

    [Fact]
    public async Task Status_UnknownId_PassesQueryFailureThroughVerbatim()
    {
        var id = AgentId.NewId();
        var notFound = RuntimeErrors.NotFound(id.Value);
        var (provider, _, _, _) = MakeProvider(ParentAtDepth(0));

        var result = await provider.InvokeAsync("status", $$"""{"id":"{{id}}"}""");

        Assert.True(result.IsError);
        Assert.Equal(notFound, result.Content);
    }

    [Fact]
    public async Task Status_MissingId_TypedArgumentError()
    {
        var (provider, _, queries, _) = MakeProvider(ParentAtDepth(0));

        var result = await provider.InvokeAsync("status", "{}");

        Assert.True(result.IsError);
        Assert.Contains("'id' must be a GUID string.", result.Content);
        Assert.Empty(queries.StatusCalls);
    }

    // --- result --------------------------------------------------------------

    [Fact]
    public async Task Result_Completed_ReturnsReportVerbatim_NoGutterNoAnnotation()
    {
        const string report = "line one\n--- not a gutter ---\nline three";
        var child = ChildIn(AgentStatus.Completed, report: report);
        var (provider, _, queries, _) = MakeProvider(ParentAtDepth(0), seed: q => q.Results[child.Id] =
            Result<string>.Success(report));

        var result = await provider.InvokeAsync("result", $$"""{"id":"{{child.Id}}"}""");

        Assert.False(result.IsError);
        Assert.Equal(report, result.Content);
        Assert.Equal(child.Id, Assert.Single(queries.ResultCalls));
    }

    [Fact]
    public async Task Result_NotComplete_PassesCanonicalErrorThroughUntouched()
    {
        var id = AgentId.NewId();
        var (provider, _, _, _) = MakeProvider(ParentAtDepth(0), seed: q => q.Results[id] =
            Result<string>.Failure(new Error("NotComplete",
                $"Agent '{id}' has not finished running. Check agent.status later.")));

        var result = await provider.InvokeAsync("result", $$"""{"id":"{{id}}"}""");

        Assert.True(result.IsError);
        Assert.Equal(RuntimeErrors.NotComplete(id.Value), result.Content);
    }

    [Fact]
    public async Task Result_NotFound_PassesCanonicalErrorThroughUntouched()
    {
        var id = AgentId.NewId();
        var (provider, _, _, _) = MakeProvider(ParentAtDepth(0), seed: q => q.Results[id] =
            Result<string>.Failure(new Error("NotFound", $"No agent exists with id '{id}'.")));

        var result = await provider.InvokeAsync("result", $$"""{"id":"{{id}}"}""");

        Assert.True(result.IsError);
        Assert.Equal(RuntimeErrors.NotFound(id.Value), result.Content);
    }

    [Fact]
    public async Task Result_MalformedGuid_TypedArgumentError_QueryNeverCalled()
    {
        var (provider, _, queries, _) = MakeProvider(ParentAtDepth(0));

        var result = await provider.InvokeAsync("result",
            """{"id":"not-a-guid"}""");

        Assert.True(result.IsError);
        Assert.Contains("'id' must be a GUID string.", result.Content);
        Assert.Empty(queries.ResultCalls);
    }

    // --- shared dispatch behavior --------------------------------------------

    [Fact]
    public async Task InvokeAsync_UnknownAction_TypedError()
    {
        var (provider, _, _, _) = MakeProvider(ParentAtDepth(0));

        var result = await provider.InvokeAsync("nope", "{}");

        Assert.True(result.IsError);
        Assert.Equal("Error [UnknownAction]: Unknown action: nope.", result.Content);
    }
}
