using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition.Tests;

/// <summary>Issue #309 end to end through the REAL adapters: the thresholds artifact in
///     workspace state, the four read ports over a real database, the pure computation, and the
///     model-facing action that renders it.
///     <para>THE TWO CRITERIA ONLY THIS LAYER CAN PROVE: 'the report is computed from persisted
///     rows' (the sources are the production ones, not fakes) and 'the same rows produce the same
///     numbers' (two builds over one database are byte-identical).</para>
///     <para>AND THE D5 ORDERING, from the outside: the action REFUSES until the artifact
///     exists, so a report cannot be produced before the bounds were fixed.</para></summary>
public sealed class ChainTelemetryCompositionTests : IDisposable
{
  private const string Ws = "C:\\ws\\telemetry";
  private static readonly DateTimeOffset T0 = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);
  private readonly string _dbPath = Path.Combine(Path.GetTempPath(),
      $"ethang-telemetry-composition-{Guid.NewGuid():N}.db");

  public void Dispose()
  {
    GC.SuppressFinalize(this);
    // Named decision (CA1031): temp-db cleanup is best effort.
#pragma warning disable CA1031, S108 // Do not catch general exception types
    try
    {
      File.Delete(_dbPath);
    }
    catch { }
#pragma warning restore CA1031, S108
  }

  private static T Must<T>(Result<T> result)
  {
    Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    return result.Value;
  }

  /// <summary>The four production read ports over one database, plus the state-backed
  ///     threshold source and the service over them — the wiring the container performs.
  ///     </summary>
  private ChainTelemetryService Service()
  {
    AppDatabase database = new(_dbPath);
    StateService state = new(new SqliteStateStore(database), new NoEvidence(),
        FixedWorkspaceContext.ForPath(Ws), new InertChangeBus(), new NoGoalRunner(),
        new NoSnapshot());
    return new ChainTelemetryService(new SqliteChainTelemetrySource(database, Ws),
        new SqliteChainSessionCostSource(database),
        new SqliteChainReviewTelemetrySource(database, Ws),
        new SqliteChainBaselineSource(database, Ws),
        new StateChainThresholdSource(state));
  }

  /// <summary>The artifact exactly as the implementing session writes it: comments carrying the
  ///     reason, then one line per metric.</summary>
  private static string Artifact() => string.Join("\n",
      ["# fixed before the first report",
       .. ChainTelemetryThresholds.RequiredKeys.Select(key => key + " = 0.5")]);

  /// <summary>Writes the artifact under the key the report derives from the chain's title,
  ///     which is what makes the two agree by construction rather than by a shared literal.
  ///     </summary>
  private async Task WriteArtifactAsync()
  {
    SqliteStateStore store = new(new AppDatabase(_dbPath));
    StateKeyValue? written = await store.SetKeyCasAsync(Ws, "sci.chain-telemetry", "thresholds",
        Artifact(), null, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.NotNull(written);
  }

  [Fact]
  public async Task BuildAsync_WithoutTheArtifact_Refuses_SoNoReportCanPrecedeTheBounds()
  {
    Result<ChainTelemetryReport> report = await Service().BuildAsync("Chain telemetry",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(report.IsSuccess);
    Assert.Equal(ChainTelemetryThresholds.Code, report.Error.Code);
    Assert.Contains("sci.chain-telemetry/thresholds", report.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task BuildAsync_WithTheArtifact_MeasuresTheRealRows_AndIsReproducible()
  {
    await WriteArtifactAsync().ConfigureAwait(true);
    Chain chain = await SeedChainAsync().ConfigureAwait(true);
    SqliteChainStore chains = new(new AppDatabase(_dbPath), Ws);
    _ = Must(await chains.SaveAsync(chain.ClaimStep(1, "session-1", T0), chain.Version,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    ChainTelemetryService service = Service();
    ChainTelemetryReport first = Must(await service.BuildAsync("Chain telemetry",
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    ChainTelemetryReport second = Must(await service.BuildAsync("Chain telemetry",
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.Equal(1, first.ChainCount);
    // THE REPRODUCIBILITY CRITERION: nothing in the read path consults a clock at report time,
    // so the same rows render identically.
    Assert.Equal(first.Render(), second.Render());
    Assert.Contains("[telemetry] 1 chain(s) in scope", first.Render(), StringComparison.Ordinal);
  }

  [Fact]
  public async Task Provider_RefusesWithAnAmbiguousChain_WhenTheWorkspaceHoldsSeveral()
  {
    await WriteArtifactAsync().ConfigureAwait(true);
    _ = await SeedChainAsync().ConfigureAwait(true);
    _ = await SeedChainAsync().ConfigureAwait(true);
    ChainTelemetryCapabilityProvider provider = new(Service(),
        new SqliteChainStore(new AppDatabase(_dbPath), Ws));

    CapabilityInvocationResult result = await provider.InvokeAsync("report", "{}",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsError);
    // Each chain carries its own thresholds artifact, so silently picking one would measure
    // every chain against bounds the user chose for another.
    Assert.Contains("ChainAmbiguous", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Provider_WithOneChain_ResolvesItsTitleAndRendersTheReport()
  {
    await WriteArtifactAsync().ConfigureAwait(true);
    _ = await SeedChainAsync().ConfigureAwait(true);
    ChainTelemetryCapabilityProvider provider = new(Service(),
        new SqliteChainStore(new AppDatabase(_dbPath), Ws));

    CapabilityInvocationResult result = await provider.InvokeAsync("report", "{}",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsError, result.Content);
    Assert.Contains("[telemetry]", result.Content, StringComparison.Ordinal);
    Assert.Contains("intervention-share", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Provider_RefusesAnUnknownParameter_RatherThanIgnoringIt()
  {
    ChainTelemetryCapabilityProvider provider = new(Service(),
        new SqliteChainStore(new AppDatabase(_dbPath), Ws));

    // Built rather than written as a literal: an escaped JSON string in C# source is a JSON
    // string the analyzer cannot tell from a real one (JSON002), and this test only needs the
    // SHAPE — one unknown parameter.
    string arguments = "{\"" + "chian" + "\":\"typo\"}";
    CapabilityInvocationResult result = await provider.InvokeAsync("report", arguments,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("Unknown parameter 'chian'", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Provider_ExposesExactlyItsDeclaredActionNames()
  {
    ChainTelemetryCapabilityProvider provider = new(Service(),
        new SqliteChainStore(new AppDatabase(_dbPath), Ws));

    Assert.Equal(
        [.. provider.Actions.Select(action => action.Name).OrderBy(name => name, StringComparer.Ordinal)],
        [.. ChainTelemetryCapabilityProvider.ActionNames.OrderBy(name => name, StringComparer.Ordinal)]);
  }

  private async Task<Chain> SeedChainAsync()
  {
    SqliteChainStore store = new(new AppDatabase(_dbPath), Ws);
    return Must(await store.CreateAsync(Chain.New(Ws, 7, 1, "Chain telemetry", "session-root",
        [1], T0), TestContext.Current.CancellationToken).ConfigureAwait(true));
  }

  // ---- inert collaborators the state service needs but this file never exercises ----

  private sealed class NoEvidence : IEvidenceRunner
  {
    public Task<EvidenceResult> RunAsync(string command, CancellationToken ct = default)
        => Task.FromResult(new EvidenceResult(command, false, "not used here"));
  }

  /// <summary>An empty change bus. The real implementation is sealed, so this is the one
  ///     double in the file — and it publishes nothing, which is exactly what the report
  ///     needs: it never writes a state key.</summary>
  private sealed class InertChangeBus : IStateChangeBus
  {
    public IDisposable Subscribe(Action<StateChange> observer)
    {
      ArgumentNullException.ThrowIfNull(observer);
      return new Lease();
    }

    public void Publish(StateChange change)
    {
      // No subscribers in this fixture.
    }

    private sealed class Lease : IDisposable
    {
      public void Dispose()
      {
        // Nothing was subscribed.
      }
    }
  }

  /// <summary>A goal runner this file never reaches: the thresholds read is the only state
  ///     access the report performs.</summary>
  private sealed class NoGoalRunner : IGoalCommandRunner
  {
    public Task<EvidenceResult> RunAsync(string workingDirectory, string command,
        CancellationToken ct = default)
        => Task.FromResult(new EvidenceResult(command, false, "not used here"));
  }

  /// <summary>A snapshot source this file never reaches, for the same reason.</summary>
  private sealed class NoSnapshot : IWorkspaceSnapshotAccess
  {
    public Task<WorkspaceSnapshot> CaptureAsync(string workspaceRoot, CancellationToken ct = default)
        => Task.FromResult(WorkspaceSnapshot.Unavailable(workspaceRoot));
  }
}
