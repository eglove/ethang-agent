using eThangAgent.AgentDomain;
using eThangAgent.PlanDomain;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>Real wiring proof: a completed/abandoned plan removes its linked todos from
///     the shared todo document through the composition-wired IPlanTodoCleaner, keeps
///     unlinked todos, and deletes the todo key once nothing remains.</summary>
[Collection("EnvironmentSensitive")]
public class PlanTodoCleanerWiringTests
{
  private static AgentSettings Settings() => new(
      new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
      new ZaiSettings(null, new Uri("https://zai.test")),
      new SubAgentOptions(null, 2));

  [Fact]
  public async Task CompletingPlan_RemovesLinkedTodos_KeepsOthers_EmptiesKey()
  {
    string dbPath = Path.Combine(Path.GetTempPath(), "ethang-plantodo-" + Guid.NewGuid().ToString("N") + ".db");
    string workspaceRoot = Directory.CreateTempSubdirectory("ethang-ws").FullName;
    Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", dbPath);
    try
    {
      AgentSessionFactory factory = new(Settings(), new AppDatabase(dbPath));
      Result<AgentSession> session = await factory.CreateAsync(workspaceRoot, Providers.OpenRouter,
          ct: TestContext.Current.CancellationToken);
      Assert.True(session.IsSuccess);

      IStateService state = session.Value.Services.GetRequiredService<IStateService>();
      PlanService plans = session.Value.Services.GetRequiredService<PlanService>();
      _ = session.Value.Services.GetRequiredService<IPlanTodoCleaner>();

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

      Result<PlanSetStatusResult> done = await plans.SetStatusAsync(plan.Value.Id, PlanStatus.Completed,
          plan.Value.Version, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
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

      await session.Value.Services.DisposeAsync().ConfigureAwait(true);
    }
    finally
    {
      Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", null);
      // Named decision (CA1031): temp cleanup is best effort.
#pragma warning disable CA1031, S108 // Do not catch general exception types
      try
      {
        File.Delete(dbPath);
      }
      catch
      {
      }

      try
      {
        Directory.Delete(workspaceRoot, true);
      }
      catch
      {
      }
#pragma warning restore CA1031, S108
    }
  }
}
