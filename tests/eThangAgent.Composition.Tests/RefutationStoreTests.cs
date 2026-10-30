using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>Real wiring proof for the workspace-visible refutation namespace (issue #315): a
///     refuted verdict recorded through the REAL experiment tool lands a real state key that the
///     workspace's own full-text search finds, and a re-record replaces rather than duplicates.
///     <para>A FAKE WOULD PROVE NOTHING HERE. The claim this feature makes is that a finding one
///     chain established is FINDABLE by another chain in the same workspace, and findability is
///     a property of the real store's FTS index - not of a method call that returned success.
///     </para></summary>
public class RefutationStoreTests
{
  private const string Ledger = "sci.alpha/ledger";
  private const string RefutationKey = "refutations/alpha-p1";

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

  /// <summary>Seeds the ledger and records a refuted verdict through the tool the COMPOSITION
  ///     wires - resolved, not constructed - so this pins the registration as well as the
  ///     behaviour. A tool resolved from DI with a missing dependency would fail here.
  ///     </summary>
  private static async Task<CapabilityInvocationResult> RecordRefutedVerdictAsync(ServiceProvider services)
  {
    IExperimentLedgerStore ledger = services.GetRequiredService<IExperimentLedgerStore>();
    _ = await ledger.AppendAsync(Ledger, "hypothesis 1: the watcher sees a no-op rewrite", null,
        TestContext.Current.CancellationToken).ConfigureAwait(false);
    _ = await ledger.AppendAsync(Ledger,
        "prediction 1 (hypothesis 1): a no-op rewrite fires Changed \u2014 falsified by: touch f.txt",
        null, TestContext.Current.CancellationToken).ConfigureAwait(false);

    // The tool is reached the way the model reaches it - through the capability registry's
    // `experiment` action - so this pins the whole production path: the registration, the
    // resolved seams, and the annotation the model reads.
    ICapabilityRegistry registry = services.GetRequiredService<ICapabilityRegistry>();
    Result<ResolvedCapability> resolved = registry.Resolve("experiment");
    Assert.True(resolved.IsSuccess, resolved.Error?.Message);
    return await registry.InvokeAsync(resolved.Value,
        /*lang=json,strict*/
        """{"timeoutSeconds":60,"action":"record-verdict","slug":"alpha","id":1,"verdict":"refuted","evidence":"2 Changed events, bytes identical"}""",
        TestContext.Current.CancellationToken).ConfigureAwait(false);
  }

  [Fact]
  public async Task ARefutedVerdict_WritesAFindableWorkspaceRefutation()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    using ServiceProvider services = Build(db);

    CapabilityInvocationResult result = await RecordRefutedVerdictAsync(services);

    Assert.False(result.IsError, result.Content);
    Assert.Contains($"[experiment] refutation shared at {RefutationKey}", result.Content,
        StringComparison.Ordinal);

    // The record is really there, and really says what the shared namespace promises.
    IStateService state = services.GetRequiredService<IStateService>();
    Result<string> stored = await state.GetAsync(RefutationKey,
        TestContext.Current.CancellationToken);
    Assert.True(stored.IsSuccess, stored.Error?.Message);
    Assert.Contains("refutation alpha#1: the watcher sees a no-op rewrite", stored.Value,
        StringComparison.Ordinal);
    Assert.Contains("\u2014 refuted: 2 Changed events, bytes identical", stored.Value, StringComparison.Ordinal);
    // D7: the stamp names what it was against. This test's workspace is not a git repository,
    // so the honest answer is the stated absence rather than a fabricated hash or a refusal.
    Assert.Contains(RefutationRecord.AgainstMarker, stored.Value, StringComparison.Ordinal);
    Assert.DoesNotContain("\u2014 against: \u2014", stored.Value, StringComparison.Ordinal);
  }

  /// <summary>THE POINT OF THE WHOLE FEATURE, measured through the real index: a session that
  ///     does not know the key can still find the finding, because state.find searches values
  ///     and key names across the workspace.</summary>
  [Fact]
  public async Task AnotherChain_FindsTheRefutation_BySearching()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    using ServiceProvider services = Build(db);
    _ = await RecordRefutedVerdictAsync(services);

    IStateService state = services.GetRequiredService<IStateService>();
    Result<IReadOnlyList<StateSearchHit>> hits = await state.SearchAsync("watcher", 20,
        TestContext.Current.CancellationToken);

    Assert.True(hits.IsSuccess, hits.Error?.Message);
    Assert.Contains(hits.Value, hit => hit.Ns == RefutationRecord.Namespace);
  }

  /// <summary>Re-recording is idempotent: the port REPLACES, so one prediction's refutation is
  ///     one key holding the current bytes rather than an accumulating pile of near-duplicates.
  ///     </summary>
  [Fact]
  public async Task RecordingTwice_ReplacesRatherThanDuplicates()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    using ServiceProvider services = Build(db);
    IRefutationStore refutations = services.GetRequiredService<IRefutationStore>();
    string key = RefutationRecord.KeyFor("alpha", 1);

    Result<int> first = await refutations.WriteAsync(key, "first",
        TestContext.Current.CancellationToken);
    Result<int> second = await refutations.WriteAsync(key, "second",
        TestContext.Current.CancellationToken);

    Assert.True(first.IsSuccess, first.Error?.Message);
    Assert.True(second.IsSuccess, second.Error?.Message);
    IStateService state = services.GetRequiredService<IStateService>();
    Result<string> stored = await state.GetAsync(key, TestContext.Current.CancellationToken);
    Assert.True(stored.IsSuccess, stored.Error?.Message);
    Assert.Equal("second", stored.Value, StringComparer.Ordinal);
  }

  /// <summary>The stamp source describes THIS workspace and never fails: a directory that is
  ///     not a git repository answers with the stated absence rather than an error the tool
  ///     would have to swallow.</summary>
  [Fact]
  public async Task TheStampSource_NamesTheHeadCommitOrStatesItsAbsence()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    using ServiceProvider services = Build(db);
    IRefutationStampSource stamps = services.GetRequiredService<IRefutationStampSource>();

    string stamp = await stamps.DescribeAsync(TestContext.Current.CancellationToken);

    Assert.False(string.IsNullOrWhiteSpace(stamp));
    // The workspace here is the literal 'app' path, which is not a repository: the honest
    // answer is the stated absence, never a blank and never a fabricated hash.
    Assert.Equal(RefutationRecord.NoCommit, stamp, StringComparer.Ordinal);
  }
}
