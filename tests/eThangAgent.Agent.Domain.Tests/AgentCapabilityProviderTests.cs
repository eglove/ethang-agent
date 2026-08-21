using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

public class AgentCapabilityProviderTests
{
    private static AgentRecord ParentAtDepth(int depth) =>
        AgentRecord.Spawned(AgentId.NewId(), null, depth, "prov/parent", null, "root task", DateTimeOffset.UtcNow);

    private static (AgentCapabilityProvider Provider, List<(AgentRecord Parent, SpawnRequest Request)> Calls)
        MakeProvider(AgentRecord parent, Result<AgentRunOutcome> outcome)
    {
        var calls = new List<(AgentRecord, SpawnRequest)>();
        var spawner = new FakeSpawner(_ =>
        {
            return outcome;
        }, calls);
        return (new AgentCapabilityProvider(spawner, () => parent), calls);
    }

    private sealed class FakeSpawner(Func<SpawnRequest, Result<AgentRunOutcome>> respond,
        List<(AgentRecord, SpawnRequest)> calls) : ISubAgentSpawner
    {
        public Task<Result<AgentRunOutcome>> SpawnAsync(AgentRecord parent, SpawnRequest request, CancellationToken ct = default)
        {
            calls.Add((parent, request));
            return Task.FromResult(respond(request));
        }
    }

    [Fact]
    public async Task Spawn_Completed_RendersGutterContract()
    {
        var outcome = new AgentRunOutcome(AgentId.NewId(), AgentStatus.Completed, null,
            "REPORT TEXT", "prov/model-x", 1);
        var (provider, calls) = MakeProvider(ParentAtDepth(0), Result<AgentRunOutcome>.Success(outcome));

        var result = await provider.InvokeAsync("spawn",
            """{"taskPrompt":"summarize","model":"prov/model-x","label":"research"}""");

        Assert.False(result.IsError);
        var id = calls[0].Request.TaskPrompt == "summarize" ? outcome.ChildId.ToString() : "";
        Assert.Contains($"[agent] id={id} status=completed depth=1 model=prov/model-x label=research", result.Content);
        Assert.Contains("--- report ---", result.Content);
        Assert.Contains("REPORT TEXT", result.Content);
        Assert.Contains("--- end report ---", result.Content);
    }

    [Fact]
    public async Task Spawn_Failed_RendersReasonAndPartialReport()
    {
        var outcome = new AgentRunOutcome(AgentId.NewId(), AgentStatus.Failed,
            AgentFailureReason.Timeout, "partial work", "prov/model-x", 1);
        var (provider, _) = MakeProvider(ParentAtDepth(0), Result<AgentRunOutcome>.Success(outcome));

        var result = await provider.InvokeAsync("spawn", """{"taskPrompt":"x"}""");

        Assert.Contains("status=failed reason=timeout", result.Content);
        Assert.Contains("partial work", result.Content);
    }

    [Fact]
    public async Task Spawn_DepthExceeded_RendersKebabReason()
    {
        var (provider, _) = MakeProvider(ParentAtDepth(2),
            Result<AgentRunOutcome>.Failure(new Error("DepthExceeded", "agent depth 2 is at the limit (3)")));

        var result = await provider.InvokeAsync("spawn", """{"taskPrompt":"x"}""");

        Assert.Contains("status=failed reason=depth-exceeded", result.Content);
        Assert.Contains("at the limit", result.Content);
    }

    [Fact]
    public async Task Spawn_MissingModel_RendersMissingModelReason()
    {
        var (provider, _) = MakeProvider(ParentAtDepth(0),
            Result<AgentRunOutcome>.Failure(new Error("MissingModel", "supply model or configure SubAgent:DefaultModel")));

        var result = await provider.InvokeAsync("spawn", """{"taskPrompt":"x"}""");

        Assert.Contains("status=failed reason=missing-model", result.Content);
    }

    [Theory]
    [InlineData("""{"taskPrompt":"x","bogus":1}""", "bogus")]
    [InlineData("""{}""", "taskPrompt")]
    [InlineData("""{"taskPrompt":"  "}""", "taskPrompt")]
    [InlineData("""{"taskPrompt":"x","model":" "}""", "model")]
    public async Task Spawn_InvalidInput_TypedErrorNamingField(string json, string expected)
    {
        var (provider, calls) = MakeProvider(ParentAtDepth(0),
            Result<AgentRunOutcome>.Success(new AgentRunOutcome(AgentId.NewId(), AgentStatus.Completed, null, "r", "m", 1)));

        var result = await provider.InvokeAsync("spawn", json);

        Assert.True(result.IsError);
        Assert.Contains(expected, result.Content);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task InvokeAsync_UnknownAction_TypedError()
    {
        var (provider, _) = MakeProvider(ParentAtDepth(0),
            Result<AgentRunOutcome>.Success(new AgentRunOutcome(AgentId.NewId(), AgentStatus.Completed, null, "r", "m", 1)));

        var result = await provider.InvokeAsync("nope", "{}");

        Assert.True(result.IsError);
        Assert.Contains("UnknownAction", result.Content);
    }

    [Fact]
    public async Task Spawn_LabelOmitted_HeaderHasNoLabelSegment()
    {
        var outcome = new AgentRunOutcome(AgentId.NewId(), AgentStatus.Completed, null, "r", "prov/m", 1);
        var (provider, _) = MakeProvider(ParentAtDepth(0), Result<AgentRunOutcome>.Success(outcome));

        var result = await provider.InvokeAsync("spawn", """{"taskPrompt":"x"}""");

        var header = result.Content.Split('\n')[0];
        Assert.DoesNotContain("label=", header);
    }
}