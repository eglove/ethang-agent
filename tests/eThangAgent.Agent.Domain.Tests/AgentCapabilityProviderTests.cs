using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

public class AgentCapabilityProviderTests
{
    private static AgentRecord ParentAtDepth(int depth) =>
        AgentRecord.Spawned(AgentId.NewId(), null, depth, "prov/parent", null, "root task", DateTimeOffset.UtcNow);

    private static (AgentCapabilityProvider Provider, List<(AgentRecord Parent, SpawnRequest Request)> Calls)
        MakeProvider(AgentRecord parent, Result<AgentId> reply)
    {
        var calls = new List<(AgentRecord, SpawnRequest)>();
        var command = new FakeSpawnCommand(reply, calls);
        return (new AgentCapabilityProvider(command, () => parent), calls);
    }

    private sealed class FakeSpawnCommand(Result<AgentId> reply,
        List<(AgentRecord Parent, SpawnRequest Request)> calls) : IAgentSpawnCommand
    {
        public Task<Result<AgentId>> Execute(AgentRecord parent, SpawnRequest request, CancellationToken ct = default)
        {
            calls.Add((parent, request));
            return Task.FromResult(reply);
        }
    }

    [Fact]
    public async Task Spawn_Success_RendersRunningLine_WithoutAnyReport()
    {
        var id = AgentId.NewId();
        var (provider, calls) = MakeProvider(ParentAtDepth(0), Result<AgentId>.Success(id));

        var result = await provider.InvokeAsync("spawn",
            """{"taskPrompt":"summarize","model":"prov/model-x","label":"research"}""");

        Assert.False(result.IsError);
        // Non-blocking contract: exactly the running line — no report gutter, no label segment.
        Assert.Equal($"[agent] id={id} status=running", result.Content);
        Assert.DoesNotContain("--- report ---", result.Content);

        var call = Assert.Single(calls);
        Assert.Equal(0, call.Parent.Depth);
        Assert.Equal("summarize", call.Request.TaskPrompt);
        Assert.Equal("prov/model-x", call.Request.Model);
        Assert.Equal("research", call.Request.Label);
    }

    [Fact]
    public async Task Spawn_HandlerFailure_PassesCanonicalErrorThroughUntouched()
    {
        var (provider, _) = MakeProvider(ParentAtDepth(2),
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
        var (provider, _) = MakeProvider(ParentAtDepth(0), Result<AgentId>.Failure(error));

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
        var (provider, calls) = MakeProvider(ParentAtDepth(0), Result<AgentId>.Success(AgentId.NewId()));

        var result = await provider.InvokeAsync("spawn", json);

        Assert.True(result.IsError);
        Assert.Contains(expected, result.Content);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task InvokeAsync_UnknownAction_TypedError()
    {
        var (provider, _) = MakeProvider(ParentAtDepth(0), Result<AgentId>.Success(AgentId.NewId()));

        var result = await provider.InvokeAsync("nope", "{}");

        Assert.True(result.IsError);
        Assert.Contains("UnknownAction", result.Content);
    }
}
