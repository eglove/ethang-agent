using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using Xunit;

namespace eThangAgent.PlanDomain.Tests;

public class PlanCapabilityProviderTests
{
  // Full copy of PlanServiceTests' private FakeStore: the store fake is small and
  // duplication beats cross-test coupling.
  private sealed class FakeStore : IPlanStore
  {
    private readonly Dictionary<int, Plan> _plans = [];
    private int _next = 1;
    public Task<Result<Plan>> CreateAsync(Plan draft, CancellationToken ct = default)
    {
      Plan saved = draft with { Id = _next++, Version = 1 };
      _plans[saved.Id] = saved;
      return Task.FromResult(Result.Success(saved));
    }
    public Task<Result<Plan>> GetAsync(int id, CancellationToken ct = default) =>
      Task.FromResult(_plans.TryGetValue(id, out Plan? p)
        ? Result.Success(p)
        : Result.Failure<Plan>(new DomainError("PlanNotFound", $"no plan #{id}")));
    public Task<Result<IReadOnlyList<Plan>>> ListAsync(PlanStatus? status, CancellationToken ct = default) =>
      Task.FromResult(Result.Success<IReadOnlyList<Plan>>([.. _plans.Values
        .Where(p => status is null || p.Status == status)]));
    public Task<Result<Plan>> SaveAsync(Plan plan, int expectedVersion, CancellationToken ct = default)
    {
      if (!_plans.TryGetValue(plan.Id, out Plan? current))
      {
        return Task.FromResult(Result.Failure<Plan>(new DomainError("PlanNotFound", $"no plan #{plan.Id}")));
      }

      if (current.Version != expectedVersion)
      {
        return Task.FromResult(Result.Failure<Plan>(new DomainError("VersionConflict",
          $"plan #{plan.Id} is at v{current.Version}; expectedVersion was {expectedVersion}")));
      }
      Plan saved = plan with { Version = current.Version + 1 };
      _plans[saved.Id] = saved;
      return Task.FromResult(Result.Success(saved));
    }
  }

  private static PlanCapabilityProvider New(out FakeStore store)
  {
    store = new FakeStore();
    return new PlanCapabilityProvider(new PlanService(store), () => "root-session-1");
  }

  private static async Task<PlanCapabilityProvider> SeededAsync()
  {
    PlanCapabilityProvider provider = New(out _);
    CapabilityInvocationResult created = await provider.InvokeAsync("create",
      /*lang=json,strict*/"{ \"title\": \"Ship\", \"goal\": \"G\", \"steps\": [ { \"title\": \"one\" }, { \"title\": \"two\", \"detail\": \"d\", \"todoId\": 3 } ] }",
      TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(created.IsError);
    return provider;
  }

  // ---- create -------------------------------------------------------------

  [Fact]
  public async Task Create_WithSteps_OutputsCreatedLine_AndStampsSession()
  {
    PlanCapabilityProvider provider = New(out _);
    string args =
      /*lang=json,strict*/"{ \"title\": \"Ship\", \"goal\": \"G\", \"steps\": [ { \"title\": \"one\" }, { \"title\": \"two\", \"detail\": \"d\", \"todoId\": 3 } ] }";
    CapabilityInvocationResult r = await provider.InvokeAsync("create", args, TestContext.Current.CancellationToken);
    Assert.False(r.IsError);
    Assert.StartsWith("[plan] created #", r.Content, StringComparison.Ordinal);
    CapabilityInvocationResult shown = await provider.InvokeAsync("show", /*lang=json,strict*/"{ \"id\": 1 }", TestContext.Current.CancellationToken);
    string get1 = shown.Content;
    Assert.Contains("session root-session-1", get1, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Create_BlankTitle_FailsInvalidActionInput()
  {
    PlanCapabilityProvider provider = New(out _);
    CapabilityInvocationResult r = await provider.InvokeAsync("create",
      /*lang=json,strict*/"{ \"title\": \"   \", \"goal\": \"G\" }", TestContext.Current.CancellationToken);
    Assert.True(r.IsError);
    Assert.StartsWith("Error [InvalidActionInput]:", r.Content, StringComparison.Ordinal);
    CapabilityInvocationResult idx = await provider.InvokeAsync("index", "{ }", TestContext.Current.CancellationToken);
    Assert.Equal("[plan] 0 plan(s)", idx.Content);
  }

  [Fact]
  public async Task Create_BlankGoal_FailsInvalidActionInput()
  {
    PlanCapabilityProvider provider = New(out _);
    CapabilityInvocationResult r = await provider.InvokeAsync("create",
      /*lang=json,strict*/"{ \"title\": \"T\", \"goal\": \"\" }", TestContext.Current.CancellationToken);
    Assert.True(r.IsError);
    Assert.StartsWith("Error [InvalidActionInput]:", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Create_StepBlankTitle_FailsInvalidActionInput()
  {
    PlanCapabilityProvider provider = New(out _);
    CapabilityInvocationResult r = await provider.InvokeAsync("create",
      /*lang=json,strict*/"{ \"title\": \"T\", \"goal\": \"G\", \"steps\": [ { \"title\": \"ok\" }, { \"title\": \" \" } ] }",
      TestContext.Current.CancellationToken);
    Assert.True(r.IsError);
    Assert.StartsWith("Error [InvalidActionInput]:", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Create_StepTodoIdBelowOne_FailsInvalidActionInput()
  {
    PlanCapabilityProvider provider = New(out _);
    CapabilityInvocationResult r = await provider.InvokeAsync("create",
      /*lang=json,strict*/"{ \"title\": \"T\", \"goal\": \"G\", \"steps\": [ { \"title\": \"s\", \"todoId\": 0 } ] }",
      TestContext.Current.CancellationToken);
    Assert.True(r.IsError);
    Assert.StartsWith("Error [InvalidActionInput]:", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Create_StepsNotArray_FailsInvalidActionInput()
  {
    PlanCapabilityProvider provider = New(out _);
    CapabilityInvocationResult r = await provider.InvokeAsync("create",
      /*lang=json,strict*/"{ \"title\": \"T\", \"goal\": \"G\", \"steps\": \"nope\" }", TestContext.Current.CancellationToken);
    Assert.True(r.IsError);
    Assert.StartsWith("Error [InvalidActionInput]:", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Create_StepNotObject_FailsInvalidActionInput()
  {
    PlanCapabilityProvider provider = New(out _);
    CapabilityInvocationResult r = await provider.InvokeAsync("create",
      /*lang=json,strict*/"{ \"title\": \"T\", \"goal\": \"G\", \"steps\": [ \"nope\" ] }", TestContext.Current.CancellationToken);
    Assert.True(r.IsError);
    Assert.StartsWith("Error [InvalidActionInput]:", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Create_UnknownParameter_FailsInvalidActionInput()
  {
    PlanCapabilityProvider provider = New(out _);
    CapabilityInvocationResult r = await provider.InvokeAsync("create",
      /*lang=json,strict*/"{ \"title\": \"T\", \"goal\": \"G\", \"bogus\": 1 }", TestContext.Current.CancellationToken);
    Assert.True(r.IsError);
    Assert.Contains("bogus", r.Content, StringComparison.Ordinal);
    Assert.StartsWith("Error [InvalidActionInput]:", r.Content, StringComparison.Ordinal);
  }

  // ---- show ---------------------------------------------------------------

  [Fact]
  public async Task Show_RendersEnvelope_Session_Steps_TodoAndDetail()
  {
    PlanCapabilityProvider provider = await SeededAsync();
    CapabilityInvocationResult shown = await provider.InvokeAsync("show", /*lang=json,strict*/"{ \"id\": 1 }", TestContext.Current.CancellationToken);
    Assert.False(shown.IsError);
    Assert.Contains("[plan #1 v1 | Active | session root-session-1] Ship", shown.Content, StringComparison.Ordinal);
    Assert.Contains("\nG\n", shown.Content, StringComparison.Ordinal);
    Assert.Contains("2 step(s):", shown.Content, StringComparison.Ordinal);
    Assert.Contains("  1. [ Pending ] one", shown.Content, StringComparison.Ordinal);
    Assert.Contains("  2. [ Pending ] two (todo #3) \u2014 d", shown.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Show_NoSteps_PrintsZeroStepHeader_AndNoStepLines()
  {
    PlanCapabilityProvider provider = New(out _);
    _ = await provider.InvokeAsync("create", /*lang=json,strict*/"{ \"title\": \"T\", \"goal\": \"G\" }", TestContext.Current.CancellationToken);
    CapabilityInvocationResult shown = await provider.InvokeAsync("show", /*lang=json,strict*/"{ \"id\": 1 }", TestContext.Current.CancellationToken);
    Assert.Contains("0 step(s):", shown.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("1. [", shown.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Show_MultiLineGoal_PrintsEveryGoalLine()
  {
    PlanCapabilityProvider provider = New(out _);
    _ = await provider.InvokeAsync("create",
      /*lang=json,strict*/"{ \"title\": \"T\", \"goal\": \"line one\\nline two\" }", TestContext.Current.CancellationToken);
    CapabilityInvocationResult shown = await provider.InvokeAsync("show", /*lang=json,strict*/"{ \"id\": 1 }", TestContext.Current.CancellationToken);
    Assert.Contains("\nline one\nline two\n", shown.Content, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("{ }")]
  [InlineData(/*lang=json,strict*/"{ \"id\": \"x\" }")]
  [InlineData(/*lang=json,strict*/"{ \"id\": 0 }")]
  public async Task Show_BadId_FailsInvalidActionInput(string args)
  {
    PlanCapabilityProvider provider = New(out _);
    CapabilityInvocationResult r = await provider.InvokeAsync("show", args, TestContext.Current.CancellationToken);
    Assert.True(r.IsError);
    Assert.StartsWith("Error [InvalidActionInput]:", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Show_UnknownPlan_FailsPlanNotFound()
  {
    PlanCapabilityProvider provider = New(out _);
    CapabilityInvocationResult r = await provider.InvokeAsync("show", /*lang=json,strict*/"{ \"id\": 9 }", TestContext.Current.CancellationToken);
    Assert.True(r.IsError);
    Assert.StartsWith("Error [PlanNotFound]:", r.Content, StringComparison.Ordinal);
  }

  // ---- index ----------------------------------------------------------------

  [Fact]
  public async Task Index_Empty_PrintsHeaderOnly()
  {
    PlanCapabilityProvider provider = New(out _);
    CapabilityInvocationResult r = await provider.InvokeAsync("index", "{ }", TestContext.Current.CancellationToken);
    Assert.False(r.IsError);
    Assert.Equal("[plan] 0 plan(s)", r.Content);
  }

  [Fact]
  public async Task Index_ListsEveryPlan_WithStepCounts_AndSessions()
  {
    PlanCapabilityProvider provider = New(out _);
    _ = await provider.InvokeAsync("create", /*lang=json,strict*/"{ \"title\": \"T1\", \"goal\": \"G\" }", TestContext.Current.CancellationToken);
    _ = await provider.InvokeAsync("create", /*lang=json,strict*/"{ \"title\": \"T2\", \"goal\": \"G\", \"steps\": [ { \"title\": \"s\" } ] }",
      TestContext.Current.CancellationToken);
    CapabilityInvocationResult r = await provider.InvokeAsync("index", "{ }", TestContext.Current.CancellationToken);
    Assert.StartsWith("[plan] 2 plan(s)", r.Content, StringComparison.Ordinal);
    Assert.Contains("#1 [ Active ] T1 (0 step(s), session root-session-1)", r.Content, StringComparison.Ordinal);
    Assert.Contains("#2 [ Active ] T2 (1 step(s), session root-session-1)", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Index_StatusFilter_ListsOnlyMatching()
  {
    PlanCapabilityProvider provider = New(out _);
    _ = await provider.InvokeAsync("create", /*lang=json,strict*/"{ \"title\": \"T1\", \"goal\": \"G\" }", TestContext.Current.CancellationToken);
    _ = await provider.InvokeAsync("create", /*lang=json,strict*/"{ \"title\": \"T2\", \"goal\": \"G\" }", TestContext.Current.CancellationToken);
    _ = await provider.InvokeAsync("set-status", /*lang=json,strict*/"{ \"id\": 1, \"status\": \"Completed\" }", TestContext.Current.CancellationToken);
    CapabilityInvocationResult done = await provider.InvokeAsync("index", /*lang=json,strict*/"{ \"status\": \"Completed\" }", TestContext.Current.CancellationToken);
    Assert.StartsWith("[plan] 1 plan(s)", done.Content, StringComparison.Ordinal);
    Assert.Contains("#1 [ Completed ] T1", done.Content, StringComparison.Ordinal);
    CapabilityInvocationResult active = await provider.InvokeAsync("index", /*lang=json,strict*/"{ \"status\": \"Active\" }", TestContext.Current.CancellationToken);
    Assert.Contains("#2 [ Active ] T2", active.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("T1", active.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Index_InvalidStatusToken_FailsInvalidActionInput()
  {
    PlanCapabilityProvider provider = New(out _);
    CapabilityInvocationResult r = await provider.InvokeAsync("index", /*lang=json,strict*/"{ \"status\": \"done\" }", TestContext.Current.CancellationToken);
    Assert.True(r.IsError);
    Assert.StartsWith("Error [InvalidActionInput]:", r.Content, StringComparison.Ordinal);
    Assert.Contains("Active", r.Content, StringComparison.Ordinal);
  }

  // ---- add-step ------------------------------------------------------------------

  [Fact]
  public async Task AddStep_Appends_AndRendersPosition()
  {
    PlanCapabilityProvider provider = await SeededAsync();
    CapabilityInvocationResult r = await provider.InvokeAsync("add-step",
      /*lang=json,strict*/"{ \"id\": 1, \"title\": \"extra\", \"todoId\": 2 }", TestContext.Current.CancellationToken);
    Assert.False(r.IsError);
    Assert.Equal("[plan] added step 3 to #1", r.Content);
    CapabilityInvocationResult shown = await provider.InvokeAsync("show", /*lang=json,strict*/"{ \"id\": 1 }", TestContext.Current.CancellationToken);
    Assert.Contains("  3. [ Pending ] extra (todo #2)", shown.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AddStep_MissingTitle_FailsInvalidActionInput()
  {
    PlanCapabilityProvider provider = await SeededAsync();
    CapabilityInvocationResult r = await provider.InvokeAsync("add-step",
      /*lang=json,strict*/"{ \"id\": 1 }", TestContext.Current.CancellationToken);
    Assert.True(r.IsError);
    Assert.StartsWith("Error [InvalidActionInput]:", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AddStep_OnFrozenPlan_FailsInvalidTransition()
  {
    PlanCapabilityProvider provider = await SeededAsync();
    _ = await provider.InvokeAsync("set-status", /*lang=json,strict*/"{ \"id\": 1, \"status\": \"Completed\" }", TestContext.Current.CancellationToken);
    CapabilityInvocationResult r = await provider.InvokeAsync("add-step",
      /*lang=json,strict*/"{ \"id\": 1, \"title\": \"extra\" }", TestContext.Current.CancellationToken);
    Assert.True(r.IsError);
    Assert.StartsWith("Error [InvalidTransition]:", r.Content, StringComparison.Ordinal);
    Assert.Contains("frozen", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AddStep_UnknownPlan_FailsPlanNotFound()
  {
    PlanCapabilityProvider provider = New(out _);
    CapabilityInvocationResult r = await provider.InvokeAsync("add-step",
      /*lang=json,strict*/"{ \"id\": 7, \"title\": \"s\" }", TestContext.Current.CancellationToken);
    Assert.True(r.IsError);
    Assert.StartsWith("Error [PlanNotFound]:", r.Content, StringComparison.Ordinal);
  }

  // ---- update-step ---------------------------------------------------------------

  [Fact]
  public async Task UpdateStep_TitleAndDetail_Renders()
  {
    PlanCapabilityProvider provider = await SeededAsync();
    CapabilityInvocationResult r = await provider.InvokeAsync("update-step",
      /*lang=json,strict*/"{ \"id\": 1, \"position\": 2, \"title\": \"rewritten\", \"detail\": \"new detail\" }",
      TestContext.Current.CancellationToken);
    Assert.False(r.IsError);
    Assert.Equal("[plan] updated step 2 in #1", r.Content);
    CapabilityInvocationResult shown = await provider.InvokeAsync("show", /*lang=json,strict*/"{ \"id\": 1 }", TestContext.Current.CancellationToken);
    Assert.Contains("  2. [ Pending ] rewritten (todo #3) \u2014 new detail", shown.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UpdateStep_Status_InProgress_Renders()
  {
    PlanCapabilityProvider provider = await SeededAsync();
    CapabilityInvocationResult r = await provider.InvokeAsync("update-step",
      /*lang=json,strict*/"{ \"id\": 1, \"position\": 1, \"status\": \"InProgress\" }", TestContext.Current.CancellationToken);
    Assert.False(r.IsError);
    Assert.Equal("[plan] updated step 1 in #1", r.Content);
    CapabilityInvocationResult shown = await provider.InvokeAsync("show", /*lang=json,strict*/"{ \"id\": 1 }", TestContext.Current.CancellationToken);
    Assert.Contains("  1. [ InProgress ] one", shown.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UpdateStep_InvalidStatusToken_FailsInvalidActionInput()
  {
    PlanCapabilityProvider provider = await SeededAsync();
    CapabilityInvocationResult r = await provider.InvokeAsync("update-step",
      /*lang=json,strict*/"{ \"id\": 1, \"position\": 1, \"status\": \"done\" }", TestContext.Current.CancellationToken);
    Assert.True(r.IsError);
    Assert.StartsWith("Error [InvalidActionInput]:", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UpdateStep_NoFields_FailsInvalidActionInput()
  {
    PlanCapabilityProvider provider = await SeededAsync();
    CapabilityInvocationResult r = await provider.InvokeAsync("update-step",
      /*lang=json,strict*/"{ \"id\": 1, \"position\": 1 }", TestContext.Current.CancellationToken);
    Assert.True(r.IsError);
    Assert.StartsWith("Error [InvalidActionInput]:", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UpdateStep_UnknownPosition_FailsPlanStepNotFound_NamingPositionAndPlan()
  {
    PlanCapabilityProvider provider = await SeededAsync();
    CapabilityInvocationResult r = await provider.InvokeAsync("update-step",
      /*lang=json,strict*/"{ \"id\": 1, \"position\": 5, \"title\": \"x\" }", TestContext.Current.CancellationToken);
    Assert.True(r.IsError);
    Assert.StartsWith("Error [PlanStepNotFound]:", r.Content, StringComparison.Ordinal);
    Assert.Contains("5", r.Content, StringComparison.Ordinal);
    Assert.Contains("#1", r.Content, StringComparison.Ordinal);
  }

  // ---- remove-step -------------------------------------------------------------

  [Fact]
  public async Task RemoveStep_Removes_AndRenumbersTail()
  {
    PlanCapabilityProvider provider = await SeededAsync();
    CapabilityInvocationResult r = await provider.InvokeAsync("remove-step",
      /*lang=json,strict*/"{ \"id\": 1, \"position\": 1 }", TestContext.Current.CancellationToken);
    Assert.False(r.IsError);
    Assert.Equal("[plan] removed step 1 from #1", r.Content);
    CapabilityInvocationResult shown = await provider.InvokeAsync("show", /*lang=json,strict*/"{ \"id\": 1 }", TestContext.Current.CancellationToken);
    Assert.Contains("1 step(s):", shown.Content, StringComparison.Ordinal);
    Assert.Contains("  1. [ Pending ] two (todo #3) \u2014 d", shown.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RemoveStep_UnknownPosition_FailsPlanStepNotFound()
  {
    PlanCapabilityProvider provider = await SeededAsync();
    CapabilityInvocationResult r = await provider.InvokeAsync("remove-step",
      /*lang=json,strict*/"{ \"id\": 1, \"position\": 9 }", TestContext.Current.CancellationToken);
    Assert.True(r.IsError);
    Assert.StartsWith("Error [PlanStepNotFound]:", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RemoveStep_MissingPosition_FailsInvalidActionInput()
  {
    PlanCapabilityProvider provider = await SeededAsync();
    CapabilityInvocationResult r = await provider.InvokeAsync("remove-step",
      /*lang=json,strict*/"{ \"id\": 1 }", TestContext.Current.CancellationToken);
    Assert.True(r.IsError);
    Assert.StartsWith("Error [InvalidActionInput]:", r.Content, StringComparison.Ordinal);
  }

  // ---- set-status -------------------------------------------------------------

  [Fact]
  public async Task SetStatus_Completes_RendersCanonicalStatus()
  {
    PlanCapabilityProvider provider = await SeededAsync();
    CapabilityInvocationResult r = await provider.InvokeAsync("set-status",
      /*lang=json,strict*/"{ \"id\": 1, \"status\": \"Completed\" }", TestContext.Current.CancellationToken);
    Assert.False(r.IsError);
    Assert.Equal("[plan] #1 is now Completed", r.Content);
    CapabilityInvocationResult shown = await provider.InvokeAsync("show", /*lang=json,strict*/"{ \"id\": 1 }", TestContext.Current.CancellationToken);
    Assert.Contains("[plan #1 v2 | Completed | session root-session-1]", shown.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SetStatus_InvalidToken_FailsInvalidActionInput()
  {
    PlanCapabilityProvider provider = await SeededAsync();
    CapabilityInvocationResult r = await provider.InvokeAsync("set-status",
      /*lang=json,strict*/"{ \"id\": 1, \"status\": \"completed\" }", TestContext.Current.CancellationToken);
    Assert.True(r.IsError);
    Assert.StartsWith("Error [InvalidActionInput]:", r.Content, StringComparison.Ordinal);
    Assert.Contains("Active", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SetStatus_MissingStatus_FailsInvalidActionInput()
  {
    PlanCapabilityProvider provider = await SeededAsync();
    CapabilityInvocationResult r = await provider.InvokeAsync("set-status",
      /*lang=json,strict*/"{ \"id\": 1 }", TestContext.Current.CancellationToken);
    Assert.True(r.IsError);
    Assert.StartsWith("Error [InvalidActionInput]:", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SetStatus_FromTerminal_FailsInvalidTransition_NamingAllowedMoves()
  {
    PlanCapabilityProvider provider = await SeededAsync();
    _ = await provider.InvokeAsync("set-status", /*lang=json,strict*/"{ \"id\": 1, \"status\": \"Completed\" }", TestContext.Current.CancellationToken);
    CapabilityInvocationResult r = await provider.InvokeAsync("set-status",
      /*lang=json,strict*/"{ \"id\": 1, \"status\": \"Active\" }", TestContext.Current.CancellationToken);
    Assert.True(r.IsError);
    Assert.StartsWith("Error [InvalidTransition]:", r.Content, StringComparison.Ordinal);
    Assert.Contains("'active'", r.Content, StringComparison.Ordinal);
    Assert.Contains("'completed'", r.Content, StringComparison.Ordinal);
    Assert.Contains("'abandoned'", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SetStatus_UnknownPlan_FailsPlanNotFound()
  {
    PlanCapabilityProvider provider = New(out _);
    CapabilityInvocationResult r = await provider.InvokeAsync("set-status",
      /*lang=json,strict*/"{ \"id\": 4, \"status\": \"Completed\" }", TestContext.Current.CancellationToken);
    Assert.True(r.IsError);
    Assert.StartsWith("Error [PlanNotFound]:", r.Content, StringComparison.Ordinal);
  }

  private static PlanCapabilityProvider NewWithCleaner(RecordingCleaner cleaner)
  {
    FakeStore store = new();
    return new PlanCapabilityProvider(new PlanService(store, cleaner), () => "root-session-1");
  }

  [Fact]
  public async Task SetStatus_TerminalWithRemovedTodos_AppendsCleanedLine()
  {
    RecordingCleaner cleaner = new() { Existing = [3] };
    PlanCapabilityProvider provider = NewWithCleaner(cleaner);
    _ = await provider.InvokeAsync("create",
      /*lang=json,strict*/"{ \"title\": \"Ship\", \"goal\": \"G\", \"steps\": [ { \"title\": \"one\", \"todoId\": 3 } ] }",
      TestContext.Current.CancellationToken);
    CapabilityInvocationResult r = await provider.InvokeAsync("set-status",
      /*lang=json,strict*/"{ \"id\": 1, \"status\": \"Completed\" }", TestContext.Current.CancellationToken);
    Assert.False(r.IsError);
    Assert.Equal("[plan] #1 is now Completed\n[plan] #1 cleaned 1 linked todo(s)", r.Content);
  }

  [Fact]
  public async Task SetStatus_CleanupFails_AppendsWarningLine_AndStillSucceeds()
  {
    RecordingCleaner cleaner = new() { FailWith = new DomainError("StorageWriteFailed", "disk full") };
    PlanCapabilityProvider provider = NewWithCleaner(cleaner);
    _ = await provider.InvokeAsync("create",
      /*lang=json,strict*/"{ \"title\": \"Ship\", \"goal\": \"G\", \"steps\": [ { \"title\": \"one\", \"todoId\": 3 } ] }",
      TestContext.Current.CancellationToken);
    CapabilityInvocationResult r = await provider.InvokeAsync("set-status",
      /*lang=json,strict*/"{ \"id\": 1, \"status\": \"Completed\" }", TestContext.Current.CancellationToken);
    Assert.False(r.IsError);
    Assert.Equal("[plan] #1 is now Completed\n[plan] warning: todo cleanup failed (StorageWriteFailed)", r.Content);
  }

  // ---- surface ---------------------------------------------------------------

  [Fact]
  public void Provider_ExposesSevenValidUniqueActions_UnderIdPlan()
  {
    PlanCapabilityProvider provider = New(out _);
    Assert.Equal("plan", provider.Id);
    string[] expected = ["create", "show", "index", "add-step", "update-step", "remove-step", "set-status"];
    Assert.Equal(expected.Length, provider.Actions.Count);
    Assert.Equal(expected, provider.Actions.Select(a => a.Name));
    Assert.All(provider.Actions, a => Assert.True(CapabilityNameRules.IsValidActionName(a.Name)));
    Assert.Equal(expected, provider.Actions.Select(a => a.Name).Distinct());
  }

  [Fact]
  public async Task UnknownAction_FailsUnknownAction()
  {
    PlanCapabilityProvider provider = New(out _);
    CapabilityInvocationResult r = await provider.InvokeAsync("rename", "{ }", TestContext.Current.CancellationToken);
    Assert.True(r.IsError);
    Assert.StartsWith("Error [UnknownAction]:", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MalformedJson_FailsInvalidActionInput()
  {
    PlanCapabilityProvider provider = New(out _);
    CapabilityInvocationResult r = await provider.InvokeAsync("create", "not json", TestContext.Current.CancellationToken);
    Assert.True(r.IsError);
    Assert.StartsWith("Error [InvalidActionInput]:", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task NullOptionalFields_Accepted_AndRenderAsAbsent()
  {
    PlanCapabilityProvider provider = New(out _);
    CapabilityInvocationResult r = await provider.InvokeAsync("add-step",
      /*lang=json,strict*/"{ \"id\": 1, \"title\": \"s\", \"detail\": null, \"todoId\": null }", TestContext.Current.CancellationToken);
    Assert.True(r.IsError); // no plan #1 yet -> PlanNotFound, proving the parse layer accepted nulls
    Assert.StartsWith("Error [PlanNotFound]:", r.Content, StringComparison.Ordinal);
    _ = await provider.InvokeAsync("create", /*lang=json,strict*/"{ \"title\": \"T\", \"goal\": \"G\" }", TestContext.Current.CancellationToken);
    CapabilityInvocationResult added = await provider.InvokeAsync("add-step",
      /*lang=json,strict*/"{ \"id\": 1, \"title\": \"s\", \"detail\": null, \"todoId\": null }", TestContext.Current.CancellationToken);
    Assert.False(added.IsError);
    CapabilityInvocationResult shown = await provider.InvokeAsync("show", /*lang=json,strict*/"{ \"id\": 1 }", TestContext.Current.CancellationToken);
    Assert.Contains("  1. [ Pending ] s", shown.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("\u2014", shown.Content, StringComparison.Ordinal);
  }
}
