using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Coverage of the agent capability actions against fake command/queries fakes:
///     spawn renders only the running line (non-blocking), status renders the state line
///     with a reason suffix when failed, result passes the query outcome through verbatim,
///     resume re-launches a settled child on the same id, ids parse strictly as Guid "D",
///     unknown actions get typed errors.</summary>
public class AgentCapabilityProviderTests
{
  private static AgentRecord ChildIn(AgentStatus status, AgentFailureReason? reason = null,
      string? report = null)
      => AgentRecord.Started(AgentId.NewId(), "prov/child", null,
              "child task", DateTimeOffset.UtcNow) with
      {
        Status = status,
        FailureReason = reason,
        CompletedAt = status is AgentStatus.Running ? null : DateTimeOffset.UtcNow,
        FinalReport = report,
      };

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

  private static (AgentCapabilityProvider Provider, FakeQueries Queries) MakeProvider(
      Action<FakeQueries>? seed = null, IAgentRuntime? runtime = null)
  {
    FakeQueries queries = new();
    seed?.Invoke(queries);
    return (new AgentCapabilityProvider(new AgentProviderServices(queries, runtime)), queries);
  }

  private sealed class FakeRuntime(AgentRunOutcome? outcome = null, DomainError? error = null, DomainError? resumeError = null) : IAgentRuntime
  {
    public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success(record.Id));

    public Task<Result<AgentRunOutcome>> WhenSettledAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(error is null
            ? Result.Success(outcome ?? new AgentRunOutcome(id, AgentStatus.Completed, null, "settled report", "prov/child"))
            : Result.Failure<AgentRunOutcome>(error));

    public List<(Guid Id, string Message)> Resumed { get; } = [];

    // Records the resume call and replies resumeError or success: the capability task builds on this fake.
    public Task<Result<AgentId>> Resume(AgentId id, string message, CancellationToken ct = default)
    {
      Resumed.Add((id.Value, message));
      return Task.FromResult(resumeError is null
          ? Result.Success(id)
          : Result.Failure<AgentId>(resumeError));
    }

    public List<(Guid Id, string Message)> Delivered { get; } = [];

    public void InterruptSubtree(AgentId rootOfSubtree) { }

    public void Interrupt(AgentId? agentId = null) { }
  }

  // --- status --------------------------------------------------------------

  [Fact]
  public async Task Status_Running_RendersBareRunningLine()
  {
    AgentRecord child = ChildIn(AgentStatus.Running);
    (AgentCapabilityProvider provider, FakeQueries queries) = MakeProvider(seed: q => q._statuses[child.Id] = Result.Success(child));

    CapabilityInvocationResult result = await provider.InvokeAsync("status", $$"""{"id":"{{child.Id}}"}""", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal($"id={child.Id} status=running", result.Content);
    Assert.Equal(child.Id, Assert.Single(queries._statusCalls));
  }

  [Fact]
  public async Task Status_Completed_RendersCompletedWithoutReasonSuffix()
  {
    AgentRecord child = ChildIn(AgentStatus.Completed, report: "the final report");
    (AgentCapabilityProvider provider, FakeQueries _) = MakeProvider(seed: q => q._statuses[child.Id] = Result.Success(child));

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
    (AgentCapabilityProvider provider, FakeQueries _) = MakeProvider(seed: q => q._statuses[child.Id] = Result.Success(child));

    CapabilityInvocationResult result = await provider.InvokeAsync("status", $$"""{"id":"{{child.Id}}"}""", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal($"id={child.Id} status=failed reason={text}", result.Content);
  }

  [Fact]
  public async Task Status_UnknownId_PassesQueryFailureThroughVerbatim()
  {
    AgentId id = AgentId.NewId();
    string notFound = RuntimeErrors.NotFound(id.Value);
    (AgentCapabilityProvider provider, FakeQueries _) = MakeProvider();

    CapabilityInvocationResult result = await provider.InvokeAsync("status", $$"""{"id":"{{id}}"}""", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Equal(notFound, result.Content);
  }

  [Fact]
  public async Task Status_MissingId_TypedArgumentError()
  {
    (AgentCapabilityProvider provider, FakeQueries queries) = MakeProvider();

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
    (AgentCapabilityProvider provider, FakeQueries queries) = MakeProvider(seed: q => q._results[child.Id] = Result.Success(report));

    CapabilityInvocationResult result = await provider.InvokeAsync("result", $$"""{"id":"{{child.Id}}"}""", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal(report, result.Content);
    Assert.Equal(child.Id, Assert.Single(queries._resultCalls));
  }

  [Fact]
  public async Task Result_NotComplete_PassesCanonicalErrorThroughUntouched()
  {
    AgentId id = AgentId.NewId();
    (AgentCapabilityProvider provider, FakeQueries _) = MakeProvider(seed: q => q._results[id] = Result.Failure<string>(new DomainError("NotComplete", $"Agent '{id}' has not finished running. Check agent.status later.")));

    CapabilityInvocationResult result = await provider.InvokeAsync("result", $$"""{"id":"{{id}}"}""", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Equal(RuntimeErrors.NotComplete(id.Value), result.Content);
  }

  [Fact]
  public async Task Result_NotFound_PassesCanonicalErrorThroughUntouched()
  {
    AgentId id = AgentId.NewId();
    (AgentCapabilityProvider provider, FakeQueries _) = MakeProvider(seed: q => q._results[id] = Result.Failure<string>(new DomainError("NotFound", $"No agent exists with id '{id}'.")));

    CapabilityInvocationResult result = await provider.InvokeAsync("result", $$"""{"id":"{{id}}"}""", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Equal(RuntimeErrors.NotFound(id.Value), result.Content);
  }

  [Fact]
  public async Task Result_MalformedGuid_TypedArgumentError_QueryNeverCalled()
  {
    (AgentCapabilityProvider provider, FakeQueries queries) = MakeProvider();

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
    (AgentCapabilityProvider provider, FakeQueries _) = MakeProvider(runtime: new FakeRuntime(new AgentRunOutcome(child.Id, AgentStatus.Completed, null, "the awaited report", "prov/child")));

    CapabilityInvocationResult result = await provider.InvokeAsync("wait", $$"""{"id":"{{child.Id}}"}""", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal("the awaited report", result.Content);
  }

  [Fact]
  public async Task Wait_NotFound_PassesThroughUntouched()
  {
    AgentId id = AgentId.NewId();
    (AgentCapabilityProvider provider, FakeQueries _) = MakeProvider(runtime: new FakeRuntime(error: new DomainError("NotFound", "no such agent.")));

    CapabilityInvocationResult result = await provider.InvokeAsync("wait", $$"""{"id":"{{id}}"}""", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Equal("Error [NotFound]: no such agent.", result.Content);
  }

  [Fact]
  public async Task Wait_CancelledWait_PassesThroughUntouched()
  {
    AgentId id = AgentId.NewId();
    (AgentCapabilityProvider provider, FakeQueries _) = MakeProvider(runtime: new FakeRuntime(error: new DomainError("Cancelled", "the wait was cancelled.")));

    CapabilityInvocationResult result = await provider.InvokeAsync("wait", $$"""{"id":"{{id}}"}""", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Equal("Error [Cancelled]: the wait was cancelled.", result.Content);
  }

  [Fact]
  public async Task Wait_FailedChild_RendersFailureAnnotation()
  {
    AgentRecord child = ChildIn(AgentStatus.Failed, AgentFailureReason.Hung);
    (AgentCapabilityProvider provider, FakeQueries _) = MakeProvider(runtime: new FakeRuntime(new AgentRunOutcome(child.Id, AgentStatus.Failed, AgentFailureReason.Hung, "", "prov/child")));

    CapabilityInvocationResult result = await provider.InvokeAsync("wait", $$"""{"id":"{{child.Id}}"}""", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Contains("hung", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Wait_MissingRuntime_TypedNotAvailableError()
  {
    (AgentCapabilityProvider provider, FakeQueries _) = MakeProvider();

    CapabilityInvocationResult result = await provider.InvokeAsync("wait", $$"""{"id":"{{AgentId.NewId()}}"}""", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Contains("NotAvailable", result.Content, StringComparison.Ordinal);
  }

  // --- resume ---------------------------------------------------------------

  [Fact]
  public async Task Resume_WithoutRuntime_FailsNotAvailable()
  {
    (AgentCapabilityProvider provider, FakeQueries _) = MakeProvider();

    CapabilityInvocationResult result = await provider.InvokeAsync("resume",
        $"{{\"id\":\"{Guid.NewGuid()}\",\"text\":\"go\"}}", TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Contains("Error [NotAvailable]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Resume_DispatchesToRuntime_AndReturnsStartReceipt()
  {
    Guid agentId = Guid.NewGuid();
    FakeRuntime runtime = new();
    (AgentCapabilityProvider provider, FakeQueries _) = MakeProvider(runtime: runtime);

    CapabilityInvocationResult result = await provider.InvokeAsync("resume",
        $"{{\"id\":\"{agentId}\",\"text\":\"fix the failing tests\"}}", TestContext.Current.CancellationToken);

    Assert.False(result.IsError, result.Content);
    Assert.Equal($"id={agentId} status=running", result.Content.Trim());
    Assert.Equal([(agentId, "fix the failing tests")], runtime.Resumed);
  }

  [Fact]
  public async Task Resume_SurfacesTheRuntimeErrorVerbatim()
  {
    FakeRuntime runtime = new(resumeError: new DomainError("NotRunning", "agent 'x' is still running"));
    (AgentCapabilityProvider provider, FakeQueries _) = MakeProvider(runtime: runtime);

    CapabilityInvocationResult result = await provider.InvokeAsync("resume",
        $"{{\"id\":\"{Guid.NewGuid()}\",\"text\":\"go\"}}", TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Contains("Error [NotRunning]", result.Content, StringComparison.Ordinal);
  }

  // The raw JSON literals below are deliberate: Resume parses raw text, so the theory
  // pins the exact malformed payloads (parser input, not constructed objects).
#pragma warning disable JSON002
  [Theory]
  [InlineData("{}", "id")]
  [InlineData("{\"id\":\"not-a-guid\",\"text\":\"go\"}", "id")]
  [InlineData("{\"id\":\"00000000-0000-0000-0000-000000000000\",\"text\":\"\"}", "text")]
  public async Task Resume_RejectsMalformedInput(string json, string member)
  {
    FakeRuntime runtime = new();
    (AgentCapabilityProvider provider, FakeQueries _) = MakeProvider(runtime: runtime);

    CapabilityInvocationResult result = await provider.InvokeAsync("resume", json, TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Contains("Error [InvalidActionInput]", result.Content, StringComparison.Ordinal);
    Assert.Contains(member, result.Content, StringComparison.Ordinal);
  }
#pragma warning restore JSON002

  // --- send auto-resume (task 5) --------------------------------------------

  // --- shared dispatch behavior --------------------------------------------

  [Fact]
  public async Task InvokeAsync_UnknownAction_TypedError()
  {
    (AgentCapabilityProvider provider, FakeQueries _) = MakeProvider();

    CapabilityInvocationResult result = await provider.InvokeAsync("nope", "{}", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Equal("Error [UnknownAction]: Unknown action: nope.", result.Content);
  }
}
