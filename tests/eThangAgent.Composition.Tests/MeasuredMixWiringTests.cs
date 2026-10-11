using System.Reflection;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using Microsoft.Extensions.DependencyInjection;

// Best-effort temp-file cleanup in finally blocks is deliberate (CA1031).
#pragma warning disable CA1031, S108 // Do not catch general exception types

namespace eThangAgent.Composition.Tests;

/// <summary>Production wiring of the measured-mix dynamic provider selection stack
///     (spec #55): the factory builds the policy/coordinator/observer trio ONLY for a
///     Price-sort session, seeds it from the workspace prior, flushes at the 25-turn
///     checkpoint and at session end, and constructs nothing for a non-Price session.
///     Assertions reach the wiring through the factory's output (the handler's
///     reselection coordinator and the root holder's usage observer) by reflection,
///     the project's existing style for private construction fields.</summary>
public class MeasuredMixWiringTests
{
  private const string Provider = Providers.OpenRouter;

  private static AgentSettings Settings() => new(
      new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
      new SubAgentOptions(null, 2));

  private static SmartSelectionOptions OptionsWith(SmartSortCriterion sort) =>
      new(null, null, null, null, null, null, sort);

  private static async Task WriteSmartSelectionAsync(AppDatabase db, string workspaceRoot, SmartSortCriterion sort)
  {
    SqliteAppPreferenceStore preferences = new(db);
    _ = await preferences.SetAsync(
        $"smart_selection:{Provider}:{workspaceRoot}", OptionsWith(sort).ToJson(),
        TestContext.Current.CancellationToken).ConfigureAwait(false);
  }

  private static object? PrivateField(object target, string name) =>
      target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target);

  private static MeasuredMixPriorWriter WriterFor(AppDatabase db, string workspaceRoot) =>
      new(new SqliteStateStore(db), MeasuredMixPrior.StateKey(Provider, workspaceRoot));

  private static async Task<MeasuredMixPrior?> PollAsync(MeasuredMixPriorWriter writer)
  {
    for (int i = 0; i < 200; i++)
    {
      MeasuredMixPrior? prior = await writer.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
      if (prior is not null)
      {
        return prior;
      }

      await Task.Delay(20, TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    return null;
  }

  [Fact]
  public async Task Wiring_PriceSort_ConstructsStack()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    DirectoryInfo dir = Directory.CreateTempSubdirectory("ethang-mm-price");
    try
    {
      string full = Path.GetFullPath(dir.FullName);
      await WriteSmartSelectionAsync(db.Database, full, SmartSortCriterion.Price);
      AgentSessionFactory factory = new(Settings(), db.Database);

      Result<AgentSession> created = await factory.CreateAsync(full, Provider, ct: TestContext.Current.CancellationToken);
      Assert.True(created.IsSuccess);

      SendMessageCommandHandler handler = created.Value.Services.GetRequiredService<SendMessageCommandHandler>();
      Assert.NotNull(PrivateField(handler, "_reselection"));

      RootAgentHolder holder = created.Value.Services.GetRequiredService<RootAgentHolder>();
      Assert.NotNull(PrivateField(holder, "_onUsage"));
    }
    finally
    {
      dir.Delete(true);
    }
  }

  [Fact]
  public async Task Wiring_NonPriceSort_ConstructsNothing()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    DirectoryInfo dir = Directory.CreateTempSubdirectory("ethang-mm-intel");
    try
    {
      string full = Path.GetFullPath(dir.FullName);
      await WriteSmartSelectionAsync(db.Database, full, SmartSortCriterion.Intelligence);
      AgentSessionFactory factory = new(Settings(), db.Database);

      Result<AgentSession> created = await factory.CreateAsync(full, Provider, ct: TestContext.Current.CancellationToken);
      Assert.True(created.IsSuccess);

      SendMessageCommandHandler handler = created.Value.Services.GetRequiredService<SendMessageCommandHandler>();
      Assert.Null(PrivateField(handler, "_reselection"));

      RootAgentHolder holder = created.Value.Services.GetRequiredService<RootAgentHolder>();
      Assert.Null(PrivateField(holder, "_onUsage"));
    }
    finally
    {
      dir.Delete(true);
    }
  }

  [Fact]
  public async Task Wiring_SeedFromPrior()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    DirectoryInfo dir = Directory.CreateTempSubdirectory("ethang-mm-seed");
    try
    {
      string full = Path.GetFullPath(dir.FullName);
      await WriteSmartSelectionAsync(db.Database, full, SmartSortCriterion.Price);
      MeasuredMixPriorWriter writer = WriterFor(db.Database, full);
      await writer.WriteAsync(new MeasuredMixPrior(1000L, 4000L, 7, DateTimeOffset.UtcNow),
          TestContext.Current.CancellationToken);

      AgentSessionFactory factory = new(Settings(), db.Database);
      Result<AgentSession> created = await factory.CreateAsync(full, Provider, ct: TestContext.Current.CancellationToken);
      Assert.True(created.IsSuccess);

      SendMessageCommandHandler handler = created.Value.Services.GetRequiredService<SendMessageCommandHandler>();
      object coordinator = PrivateField(handler, "_reselection")!;
      MeasuredMixPolicy policy = (MeasuredMixPolicy)PrivateField(coordinator, "_policy")!;

      // SeedWeight = TotalOutput / TotalInput = 4000 / 1000 = 4.
      Assert.Equal(4m, policy.MeasuredWeight);
    }
    finally
    {
      dir.Delete(true);
    }
  }

  [Fact]
  public async Task Wiring_CheckpointFlush()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    DirectoryInfo dir = Directory.CreateTempSubdirectory("ethang-mm-checkpoint");
    try
    {
      string full = Path.GetFullPath(dir.FullName);
      await WriteSmartSelectionAsync(db.Database, full, SmartSortCriterion.Price);
      AgentSessionFactory factory = new(Settings(), db.Database);

      Result<AgentSession> created = await factory.CreateAsync(full, Provider, ct: TestContext.Current.CancellationToken);
      Assert.True(created.IsSuccess);

      RootAgentHolder holder = created.Value.Services.GetRequiredService<RootAgentHolder>();
      Action<TokenUsage> onUsage = (Action<TokenUsage>)PrivateField(holder, "_onUsage")!;
      for (int i = 0; i < 25; i++)
      {
        onUsage(new TokenUsage(100, 50));
      }

      MeasuredMixPrior? prior = await PollAsync(WriterFor(db.Database, full));
      Assert.NotNull(prior);
      Assert.Equal(25, prior.TurnCount);
      Assert.Equal(2500L, prior.TotalInput);
      Assert.Equal(1250L, prior.TotalOutput);
    }
    finally
    {
      dir.Delete(true);
    }
  }

  [Fact]
  public async Task Wiring_SessionEndFlush()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    DirectoryInfo dir = Directory.CreateTempSubdirectory("ethang-mm-end");
    try
    {
      string full = Path.GetFullPath(dir.FullName);
      await WriteSmartSelectionAsync(db.Database, full, SmartSortCriterion.Price);
      AgentSessionFactory factory = new(Settings(), db.Database);

      Result<AgentSession> created = await factory.CreateAsync(full, Provider, ct: TestContext.Current.CancellationToken);
      Assert.True(created.IsSuccess);

      RootAgentHolder holder = created.Value.Services.GetRequiredService<RootAgentHolder>();
      Action<TokenUsage> onUsage = (Action<TokenUsage>)PrivateField(holder, "_onUsage")!;
      for (int i = 0; i < 3; i++)
      {
        onUsage(new TokenUsage(100, 50));
      }

      await created.Value.Services.DisposeAsync();

      MeasuredMixPrior? prior = await WriterFor(db.Database, full).ReadAsync(TestContext.Current.CancellationToken);
      Assert.NotNull(prior);
      Assert.Equal(3, prior.TurnCount);
      Assert.Equal(300L, prior.TotalInput);
      Assert.Equal(150L, prior.TotalOutput);
    }
    finally
    {
      dir.Delete(true);
    }
  }
}
