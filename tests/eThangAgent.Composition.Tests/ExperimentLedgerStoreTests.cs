using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>Real wiring proof for the experiment ledger seam: the composition-wired
///     IExperimentLedgerStore appends single lines to a state key atomically, reads them
///     back in append order, and fails closed on a stale expected version or on text its
///     underlying store rejects. A real state store backs it — a fake would prove nothing
///     about the append contract the experiment tool depends on.</summary>
public class ExperimentLedgerStoreTests
{
  private const string Key = "sci.alpha/ledger";

  private static ServiceProvider Build(TestAppDatabase db) =>
      new ServiceCollection()
        .AddEThangAgentCore(
            new AgentSettings(
                new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
                new SubAgentOptions(null, 2)),
            Providers.OpenRouter,
            ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!,
            new AgentHostOptions(FixedWorkspaceContext.ForPath("app"), new UnrootedPathResolver()),
            db.Database)
        .BuildServiceProvider();

  [Fact]
  public async Task AppendThenRead_RoundTripsBothLinesInAppendOrder()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    using ServiceProvider services = Build(db);
    IExperimentLedgerStore ledger = services.GetRequiredService<IExperimentLedgerStore>();
    _ = Assert.IsType<StateServiceExperimentLedgerStore>(ledger);

    Result<int> first = await ledger.AppendAsync(Key, "hypothesis 1: alpha", null,
        TestContext.Current.CancellationToken);
    Assert.True(first.IsSuccess);
    Assert.Equal(1, first.Value);

    Result<string> oneLine = await ledger.ReadAsync(Key, TestContext.Current.CancellationToken);
    Assert.True(oneLine.IsSuccess);
    Assert.Equal("hypothesis 1: alpha", oneLine.Value);

    Result<int> second = await ledger.AppendAsync(Key,
        "prediction 1 (hypothesis 1): beta — falsified by: gamma", null,
        TestContext.Current.CancellationToken);
    Assert.True(second.IsSuccess);
    Assert.Equal(2, second.Value);

    Result<string> twoLines = await ledger.ReadAsync(Key, TestContext.Current.CancellationToken);
    Assert.True(twoLines.IsSuccess);
    Assert.Equal(
        "hypothesis 1: alpha\nprediction 1 (hypothesis 1): beta — falsified by: gamma",
        twoLines.Value);
  }

  [Fact]
  public async Task FiveAppends_ReadBackInOrder_WithVersionsAdvancingByOne()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    using ServiceProvider services = Build(db);
    IExperimentLedgerStore ledger = services.GetRequiredService<IExperimentLedgerStore>();

    string[] appended = ["line one", "line two", "line three", "line four", "line five"];
    for (int i = 0; i < appended.Length; i++)
    {
      Result<int> write = await ledger.AppendAsync(Key, appended[i], null,
          TestContext.Current.CancellationToken);
      Assert.True(write.IsSuccess);
      Assert.Equal(i + 1, write.Value);
    }

    Result<string> read = await ledger.ReadAsync(Key, TestContext.Current.CancellationToken);
    Assert.True(read.IsSuccess);
    Assert.Equal(string.Join("\n", appended), read.Value);
    Assert.Equal(appended, read.Value.Split('\n'));
  }

  [Fact]
  public async Task StaleExpectedVersion_FailsClosed_AndLeavesTheValueUnchanged()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    using ServiceProvider services = Build(db);
    IExperimentLedgerStore ledger = services.GetRequiredService<IExperimentLedgerStore>();

    Result<int> created = await ledger.AppendAsync(Key, "hypothesis 1: alpha", null,
        TestContext.Current.CancellationToken);
    Assert.True(created.IsSuccess);

    Result<int> stale = await ledger.AppendAsync(Key, "hypothesis 2: delta", 99,
        TestContext.Current.CancellationToken);
    Assert.False(stale.IsSuccess);
    Assert.Equal("VersionConflict", stale.Error.Code);

    Result<string> unchanged = await ledger.ReadAsync(Key, TestContext.Current.CancellationToken);
    Assert.True(unchanged.IsSuccess);
    Assert.Equal("hypothesis 1: alpha", unchanged.Value);

    // A missing key with an expected version conflicts instead of being created.
    const string FreshKey = "sci.beta/ledger";
    Result<int> missing = await ledger.AppendAsync(FreshKey, "hypothesis 1: epsilon", 1,
        TestContext.Current.CancellationToken);
    Assert.False(missing.IsSuccess);
    Assert.Equal("VersionConflict", missing.Error.Code);

    Result<string> absent = await ledger.ReadAsync(FreshKey, TestContext.Current.CancellationToken);
    Assert.False(absent.IsSuccess);
    Assert.Equal("KeyNotFound", absent.Error.Code);
  }

  [Fact]
  public async Task AppendWithNewline_FailsInvalidText_AndLeavesTheKeyUnchanged()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    using ServiceProvider services = Build(db);
    IExperimentLedgerStore ledger = services.GetRequiredService<IExperimentLedgerStore>();

    Result<int> created = await ledger.AppendAsync(Key, "hypothesis 1: alpha", null,
        TestContext.Current.CancellationToken);
    Assert.True(created.IsSuccess);

    Result<int> multiline = await ledger.AppendAsync(Key, "line one\nline two", null,
        TestContext.Current.CancellationToken);
    Assert.False(multiline.IsSuccess);
    Assert.Equal("InvalidText", multiline.Error.Code);

    Result<string> unchanged = await ledger.ReadAsync(Key, TestContext.Current.CancellationToken);
    Assert.True(unchanged.IsSuccess);
    Assert.Equal("hypothesis 1: alpha", unchanged.Value);
  }
}
