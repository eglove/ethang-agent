using eThangAgent.AgentDomain;
using eThangAgent.PlanDomain;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>Real wiring proof: a completed/abandoned plan removes its linked todos from
///     the shared todo document through the composition-wired IPlanTodoCleaner, keeps
///     unlinked todos, and deletes the todo key once nothing remains.
///     <para>Derives from <see cref="SessionOwningTest"/> (issue #284 item 3): the session
///     owns a real ChildHost process, and disposing it from IAsyncLifetime means a failing
///     assertion can no longer leave that host running. This file is the one where the
///     defect was observed - an invariant change made SetStatusAsync return a failure, the
///     assertion fired, and a ChildHost survived holding its SQLite file.</para></summary>
[Collection("EnvironmentSensitive")]
public class PlanTodoCleanerWiringTests : SessionOwningTest
{
  private static AgentSettings Settings() => new(
      new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
      new SubAgentOptions(null, 2));

  [Fact]
  public async Task CompletingPlan_RemovesLinkedTodos_KeepsOthers_EmptiesKey()
  {
    AgentSession session = await CreateSessionAsync(Settings());

    IStateService state = session.Services.GetRequiredService<IStateService>();
    PlanService plans = session.Services.GetRequiredService<PlanService>();
    _ = session.Services.GetRequiredService<IPlanTodoCleaner>();

    Result<StateKeyValue> seeded = await state.SetAsync(TodoTool.StoreKey,
        TodoDocument.Serialize(
        [
            new TodoItem(1, "linked", TodoStatus.InProgress),
            new TodoItem(2, "other", TodoStatus.Pending),
        ]), null, TestContext.Current.CancellationToken);
    Assert.True(seeded.IsSuccess);

    Result<Plan> plan = await plans.CreateAsync("Ship", "G", "sess-1",
        [("step", null, 1)], DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
    Assert.True(plan.IsSuccess);

    // Completion demands a consistent plan (issue #271): the step must be Done or
    // Skipped first, or set-status refuses and no todo cleanup runs.
    Result<Plan> finished = await plans.UpdateStepAsync(plan.Value.Id, 1,
        s => s with { Status = PlanStepStatus.Done }, plan.Value.Version,
        DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
    Assert.True(finished.IsSuccess);

    Result<PlanSetStatusResult> done = await plans.SetStatusAsync(plan.Value.Id, PlanStatus.Completed,
        finished.Value.Version, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
    Assert.True(done.IsSuccess);
    Assert.Equal([1], done.Value.RemovedTodoIds);
    Assert.Null(done.Value.CleanupError);

    Result<string> after = await state.GetAsync(TodoTool.StoreKey, TestContext.Current.CancellationToken);
    Assert.True(after.IsSuccess);
    Result<IReadOnlyList<TodoItem>> doc = TodoDocument.Parse(after.Value);
    Assert.True(doc.IsSuccess);
    _ = Assert.Single(doc.Value, i => i.Id == 2);

    // Second terminal plan, now removing the last linked todo: the key must be gone.
    Result<Plan> second = await plans.CreateAsync("Rest", "G", "sess-1",
        [("step", null, 2)], DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
    Assert.True(second.IsSuccess);
    Result<PlanSetStatusResult> done2 = await plans.SetStatusAsync(second.Value.Id, PlanStatus.Abandoned,
        second.Value.Version, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
    Assert.True(done2.IsSuccess);
    Assert.Equal([2], done2.Value.RemovedTodoIds);

    Result<string> gone = await state.GetAsync(TodoTool.StoreKey, TestContext.Current.CancellationToken);
    Assert.False(gone.IsSuccess);
    Assert.Equal("KeyNotFound", gone.Error.Code);
  }
}
