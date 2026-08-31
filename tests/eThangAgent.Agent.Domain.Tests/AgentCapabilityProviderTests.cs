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
    public readonly Dictionary<AgentId, Result<AgentRecord>> _statuses = [];
    public readonly Dictionary<AgentId, Result<string>> _results = [];
    public readonly List<AgentId> _statusCalls = [];
    public readonly List<AgentId> _resultCalls = [];

    public Task<Result<AgentRecord>> GetStatus(AgentId id, CancellationToken ct = default)
    {
      _statusCalls.Add(id);
      return Task.FromResult(_statuses.TryGetValue(id, out Result<AgentRecord>? result)
          ? result
          : Result.Failure<AgentRecord>(new DomainError("NotFound", $"No agent exists with id '{id}'.")));
    }

    public Task<Result<string>> GetResult(AgentId id, CancellationToken ct = default)
    {
      _resultCalls.Add(id);
      return Task.FromResult(_results.TryGetValue(id, out Result<string>? result)
          ? result
          : Result.Failure<string>(new DomainError("NotFound", $"No agent exists with id '{id}'.")));
    }
  }

  private static (AgentCapabilityProvider Provider, FakeSpawnCommand Command, FakeQueries Queries,
      List<(AgentRecord Parent, SpawnRequest Request)> Calls)
      MakeProvider(AgentRecord parent,
          Result<AgentId>? spawnReply = null,
          Action<FakeQueries>? seed = null,
          IAgentRuntime? runtime = null)
  {
    List<(AgentRecord, SpawnRequest)> calls = [];
    FakeSpawnCommand command = new(spawnReply ?? Result.Success(AgentId.NewId()), calls);
    FakeQueries queries = new();
    seed?.Invoke(queries);
    return (new AgentCapabilityProvider(command, queries, () => parent, runtime), command, queries, calls);
  }

  private sealed class FakeRuntime(AgentRunOutcome? outcome = null, DomainError? error = null) : IAgentRuntime
  {
    public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success(record.Id));

    public Task<Result<AgentRunOutcome>> WhenSettledAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(error is null
            ? Result.Success(outcome ?? new AgentRunOutcome(id, AgentStatus.Completed, null, "settled report", "prov/child", 1))
            : Result.Failure<AgentRunOutcome>(error));

    public Result<bool> Deliver(AgentId id, PendingMessage message)
        => Result.Success(true);

    public void Interrupt(AgentId? childId = null) { }
  }

  // --- spawn ---------------------------------------------------------------

  [Fact]
  public async Task Spawn_Success_RendersRunningLine_WithoutAnyReport()
  {
    AgentId id = AgentId.NewId();
    AgentRecord parent = ParentAtDepth(0);
    (AgentCapabilityProvider? provider, FakeSpawnCommand _, FakeQueries _, List<(AgentRecord Parent, SpawnRequest Request)>? calls) = MakeProvider(parent, Result.Success(id));

    CapabilityInvocationResult result = await provider.InvokeAsync("spawn",
                             /*lang=json,strict*/
                             """{"taskPrompt":"summarize","model":"prov/model-x","label":"research"}""", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    // Non-blocking contract: exactly the running line — no report gutter, no label segment.
    Assert.Equal($"id={id} status=running", result.Content);
    Assert.DoesNotContain("--- report ---", result.Content, StringComparison.Ordinal);

    (AgentRecord? Parent, SpawnRequest? Request) = Assert.Single(calls);
    Assert.Equal(parent, Parent);
    Assert.Equal("summarize", Request.TaskPrompt);
    Assert.Equal("prov/model-x", Request.Model);
    Assert.Equal("research", Request.Label);
  }

  [Fact]
  public async Task Spawn_HandlerFailure_PassesCanonicalErrorThroughUntouched()
  {
    (AgentCapabilityProvider? provider, FakeSpawnCommand _, FakeQueries _, List<(AgentRecord Parent, SpawnRequest Request)> _) = MakeProvider(ParentAtDepth(2),
        Result.Failure<AgentId>(new DomainError("DepthExceeded",
            "agent depth 2 is at the limit (3); children cannot spawn further")));

    CapabilityInvocationResult result = await provider.InvokeAsync("spawn", /*lang=json,strict*/ """{"taskPrompt":"x"}""", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Equal("Error [DepthExceeded]: agent depth 2 is at the limit (3); children cannot spawn further",
        result.Content);
  }

  [Fact]
  public async Task Spawn_CapReached_CanonicalStringRoundTripsByteForByte()
  {
    // The runtime builds its cap Error by parsing RuntimeErrors.CapReached; rendering must
    // reproduce that canonical string exactly so callers can match on it.
    string canonical = RuntimeErrors.CapReached;
    int codeStart = canonical.IndexOf('[', StringComparison.Ordinal) + 1;
    int codeEnd = canonical.IndexOf(']', codeStart);
    DomainError error = new(canonical[codeStart..codeEnd], canonical[(codeEnd + 3)..]);
    (AgentCapabilityProvider? provider, FakeSpawnCommand _, FakeQueries _, List<(AgentRecord Parent, SpawnRequest Request)> _) = MakeProvider(ParentAtDepth(0), Result.Failure<AgentId>(error));

    CapabilityInvocationResult result = await provider.InvokeAsync("spawn", /*lang=json,strict*/ """{"taskPrompt":"x"}""", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Equal(canonical, result.Content);
  }

  [Theory]
  [InlineData(/*lang=json,strict*/ """{"taskPrompt":"x","bogus":1}""", "bogus")]
  [InlineData("""{}""", "taskPrompt")]
  [InlineData(/*lang=json,strict*/ """{"taskPrompt":"  "}""", "taskPrompt")]
  [InlineData(/*lang=json,strict*/ """{"taskPrompt":"x","model":" "}""", "model")]
  public async Task Spawn_InvalidInput_TypedErrorNamingField(string json, string expected)
  {
    (AgentCapabilityProvider? provider, FakeSpawnCommand _, FakeQueries _, List<(AgentRecord Parent, SpawnRequest Request)>? calls) = MakeProvider(ParentAtDepth(0));

    CapabilityInvocationResult result = await provider.InvokeAsync("spawn", json, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Contains(expected, result.Content, StringComparison.Ordinal);
    Assert.Empty(calls);
  }

  // --- status --------------------------------------------------------------

  [Fact]
  public async Task Status_Running_RendersBareRunningLine()
  {
    AgentRecord child = ChildIn(AgentStatus.Running);
    (AgentCapabilityProvider? provider, FakeSpawnCommand _, FakeQueries? queries, List<(AgentRecord Parent, SpawnRequest Request)> _) = MakeProvider(ParentAtDepth(0), seed: q => q._statuses[child.Id] =
        Result.Success(child));

    CapabilityInvocationResult result = await provider.InvokeAsync("status", $$"""{"id":"{{child.Id}}"}""", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal($"id={child.Id} status=running", result.Content);
    Assert.Equal(child.Id, Assert.Single(queries._statusCalls));
  }

  [Fact]
  public async Task Status_Completed_RendersCompletedWithoutReasonSuffix()
  {
    AgentRecord child = ChildIn(AgentStatus.Completed, report: "the final report");
    (AgentCapabilityProvider? provider, FakeSpawnCommand _, FakeQueries _, List<(AgentRecord Parent, SpawnRequest Request)> _) = MakeProvider(ParentAtDepth(0), seed: q => q._statuses[child.Id] =
        Result.Success(child));

    CapabilityInvocationResult result = await provider.InvokeAsync("status", $$"""{"id":"{{child.Id}}"}""", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal($"id={child.Id} status=completed", result.Content);
  }

  [Theory]
  [InlineData(AgentFailureReason.MaxIterations, "max-iterations")]
  [InlineData(AgentFailureReason.Timeout, "timeout")]
  [InlineData(AgentFailureReason.ProviderError, "provider-error")]
  public async Task Status_Failed_RendersStateLineWithReasonSuffix(AgentFailureReason reason, string text)
  {
    AgentRecord child = ChildIn(AgentStatus.Failed, reason);
    (AgentCapabilityProvider? provider, FakeSpawnCommand _, FakeQueries _, List<(AgentRecord Parent, SpawnRequest Request)> _) = MakeProvider(ParentAtDepth(0), seed: q => q._statuses[child.Id] =
        Result.Success(child));

    CapabilityInvocationResult result = await provider.InvokeAsync("status", $$"""{"id":"{{child.Id}}"}""", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal($"id={child.Id} status=failed reason={text}", result.Content);
  }

  [Fact]
  public async Task Status_UnknownId_PassesQueryFailureThroughVerbatim()
  {
    AgentId id = AgentId.NewId();
    string notFound = RuntimeErrors.NotFound(id.Value);
    (AgentCapabilityProvider? provider, FakeSpawnCommand _, FakeQueries _, List<(AgentRecord Parent, SpawnRequest Request)> _) = MakeProvider(ParentAtDepth(0));

    CapabilityInvocationResult result = await provider.InvokeAsync("status", $$"""{"id":"{{id}}"}""", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Equal(notFound, result.Content);
  }

  [Fact]
  public async Task Status_MissingId_TypedArgumentError()
  {
    (AgentCapabilityProvider? provider, FakeSpawnCommand _, FakeQueries? queries, List<(AgentRecord Parent, SpawnRequest Request)> _) = MakeProvider(ParentAtDepth(0));

    CapabilityInvocationResult result = await provider.InvokeAsync("status", "{}", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Contains("'id' must be a GUID string.", result.Content, StringComparison.Ordinal);
    Assert.Empty(queries._statusCalls);
  }

  // --- result --------------------------------------------------------------

  [Fact]
  public async Task Result_Completed_ReturnsReportVerbatim_NoGutterNoAnnotation()
  {
    const string report = "line one\n--- not a gutter ---\nline three";
    AgentRecord child = ChildIn(AgentStatus.Completed, report: report);
    (AgentCapabilityProvider? provider, FakeSpawnCommand _, FakeQueries? queries, List<(AgentRecord Parent, SpawnRequest Request)> _) = MakeProvider(ParentAtDepth(0), seed: q => q._results[child.Id] =
        Result.Success(report));

    CapabilityInvocationResult result = await provider.InvokeAsync("result", $$"""{"id":"{{child.Id}}"}""", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal(report, result.Content);
    Assert.Equal(child.Id, Assert.Single(queries._resultCalls));
  }

  [Fact]
  public async Task Result_NotComplete_PassesCanonicalErrorThroughUntouched()
  {
    AgentId id = AgentId.NewId();
    (AgentCapabilityProvider? provider, FakeSpawnCommand _, FakeQueries _, List<(AgentRecord Parent, SpawnRequest Request)> _) = MakeProvider(ParentAtDepth(0), seed: q => q._results[id] =
        Result.Failure<string>(new DomainError("NotComplete",
            $"Agent '{id}' has not finished running. Check agent.status later.")));

    CapabilityInvocationResult result = await provider.InvokeAsync("result", $$"""{"id":"{{id}}"}""", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Equal(RuntimeErrors.NotComplete(id.Value), result.Content);
  }

  [Fact]
  public async Task Result_NotFound_PassesCanonicalErrorThroughUntouched()
  {
    AgentId id = AgentId.NewId();
    (AgentCapabilityProvider? provider, FakeSpawnCommand _, FakeQueries _, List<(AgentRecord Parent, SpawnRequest Request)> _) = MakeProvider(ParentAtDepth(0), seed: q => q._results[id] =
        Result.Failure<string>(new DomainError("NotFound", $"No agent exists with id '{id}'.")));

    CapabilityInvocationResult result = await provider.InvokeAsync("result", $$"""{"id":"{{id}}"}""", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Equal(RuntimeErrors.NotFound(id.Value), result.Content);
  }

  [Fact]
  public async Task Result_MalformedGuid_TypedArgumentError_QueryNeverCalled()
  {
    (AgentCapabilityProvider? provider, FakeSpawnCommand _, FakeQueries? queries, List<(AgentRecord Parent, SpawnRequest Request)> _) = MakeProvider(ParentAtDepth(0));

    CapabilityInvocationResult result = await provider.InvokeAsync("result",
                             /*lang=json,strict*/
                             """{"id":"not-a-guid"}""", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Contains("'id' must be a GUID string.", result.Content, StringComparison.Ordinal);
    Assert.Empty(queries._resultCalls);
  }

  // --- wait ----------------------------------------------------------------

  [Fact]
  public async Task Wait_CompletedChild_RendersOutcomeReportVerbatim()
  {
    AgentRecord child = ChildIn(AgentStatus.Completed, report: "the awaited report");
    (AgentCapabilityProvider? provider, FakeSpawnCommand _, FakeQueries _, List<(AgentRecord Parent, SpawnRequest Request)> _) = MakeProvider(
        ParentAtDepth(0), runtime: new FakeRuntime(new AgentRunOutcome(child.Id, AgentStatus.Completed, null, "the awaited report", "prov/child", 1)));

    CapabilityInvocationResult result = await provider.InvokeAsync("wait", $$"""{"id":"{{child.Id}}"}""", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal("the awaited report", result.Content);
  }

  [Fact]
  public async Task Wait_NotFound_PassesThroughUntouched()
  {
    AgentId id = AgentId.NewId();
    (AgentCapabilityProvider? provider, FakeSpawnCommand _, FakeQueries _, List<(AgentRecord Parent, SpawnRequest Request)> _) = MakeProvider(
        ParentAtDepth(0), runtime: new FakeRuntime(error: new DomainError("NotFound", "no such agent.")));

    CapabilityInvocationResult result = await provider.InvokeAsync("wait", $$"""{"id":"{{id}}"}""", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Equal("Error [NotFound]: no such agent.", result.Content);
  }

  [Fact]
  public async Task Wait_CancelledWait_PassesThroughUntouched()
  {
    AgentId id = AgentId.NewId();
    (AgentCapabilityProvider? provider, FakeSpawnCommand _, FakeQueries _, List<(AgentRecord Parent, SpawnRequest Request)> _) = MakeProvider(
        ParentAtDepth(0), runtime: new FakeRuntime(error: new DomainError("Cancelled", "the wait was cancelled.")));

    CapabilityInvocationResult result = await provider.InvokeAsync("wait", $$"""{"id":"{{id}}"}""", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Equal("Error [Cancelled]: the wait was cancelled.", result.Content);
  }

  [Fact]
  public async Task Wait_FailedChild_RendersFailureAnnotation()
  {
    AgentRecord child = ChildIn(AgentStatus.Failed, AgentFailureReason.Hung);
    (AgentCapabilityProvider? provider, FakeSpawnCommand _, FakeQueries _, List<(AgentRecord Parent, SpawnRequest Request)> _) = MakeProvider(
        ParentAtDepth(0), runtime: new FakeRuntime(new AgentRunOutcome(child.Id, AgentStatus.Failed, AgentFailureReason.Hung, "", "prov/child", 1)));

    CapabilityInvocationResult result = await provider.InvokeAsync("wait", $$"""{"id":"{{child.Id}}"}""", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Contains("hung", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Wait_MissingRuntime_TypedNotAvailableError()
  {
    (AgentCapabilityProvider? provider, FakeSpawnCommand _, FakeQueries _, List<(AgentRecord Parent, SpawnRequest Request)> _) = MakeProvider(ParentAtDepth(0));

    CapabilityInvocationResult result = await provider.InvokeAsync("wait", $$"""{"id":"{{AgentId.NewId()}}"}""", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Contains("NotAvailable", result.Content, StringComparison.Ordinal);
  }
  // --- shared dispatch behavior --------------------------------------------

  [Fact]
  public async Task InvokeAsync_UnknownAction_TypedError()
  {
    (AgentCapabilityProvider? provider, FakeSpawnCommand _, FakeQueries _, List<(AgentRecord Parent, SpawnRequest Request)> _) = MakeProvider(ParentAtDepth(0));

    CapabilityInvocationResult result = await provider.InvokeAsync("nope", "{}", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Equal("Error [UnknownAction]: Unknown action: nope.", result.Content);
  }
}
