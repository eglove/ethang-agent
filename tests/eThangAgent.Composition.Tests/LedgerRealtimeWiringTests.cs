using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>The workbench's ledger view is event-driven (issue #318 D4). Two things must hold for
///     it to be INSTANT in production, and the plan and chain views proved both can be silently
///     absent:
///     <list type="number">
///     <item>a state write must publish a <see cref="StateChangedEvent"/> onto the session's
///     event stream, because the ledger is appended by the experiment tool in the ChildHost
///     process - the app container's <see cref="IStateChangeBus"/> is IN-PROCESS and session-
///     scoped, so a view subscribing it directly would listen to a bus nobody publishes on;</item>
///     <item>the publisher must be WIRED, and DI is lazy, so nothing resolving it by name would
///     mean it subscribes to nothing and every state change fires into no listener.</item>
///     </list>
///     <para>The second is proven the way the chain view's is: resolving the service is what
///     constructs the publisher, and the publish tests below are the behavioral proof that the
///     subscription is live.</para></summary>
public class LedgerRealtimeWiringTests
{
  private static ServiceProvider Build()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new SubAgentOptions(null, 2));
    return new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!,
            new AgentHostOptions(
                FixedWorkspaceContext.ForPath("app"), new UnrootedPathResolver()), db.Database)
        .BuildServiceProvider();
  }

  /// <summary>Resolving the state service constructs the publisher inside its own factory, which
  ///     is the only ordering that cannot be forgotten (a separate registration is never
  ///     resolved, because nothing asks for it by name).</summary>
  [Fact]
  public void Container_WiresTheStateChangePublisherOntoTheService()
  {
    using ServiceProvider services = Build();

    Assert.NotNull(services.GetService<IStateService>());
  }

  /// <summary>The behavioral proof: a state SET through the container's own service reaches the
  ///     session's stream as a <see cref="StateChangedEvent"/>, so a view subscribing that stream
  ///     sees it without polling.</summary>
  [Fact]
  public async Task StateSet_PublishesAStateChangedEvent()
  {
    using ServiceProvider services = Build();
    IAgentEvents events = services.GetRequiredService<IAgentEvents>();
    IStateService state = services.GetRequiredService<IStateService>();
    RecordingSubscriber recorder = new();
    using IDisposable lease = events.Subscribe(recorder);

    Result<StateKeyValue> saved = await state
        .SetAsync("sci.318-ledger-view/ledger", "hypothesis 1: x", null,
            TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.True(saved.IsSuccess, saved.Error?.Message ?? "expected success");

    StateChangedEvent change = Assert.Single(recorder.Events.OfType<StateChangedEvent>());
    Assert.Equal("sci.318-ledger-view/ledger", change.Key);
    Assert.Equal("set", change.Change);
  }

  /// <summary>An APPEND is the fact the ledger view actually lives on: the experiment tool
  ///     appends one line per record, so every record after the first arrives as an append.
  ///     <para>IT PUBLISHES AS 'set', AND THAT IS THE TRUTH RATHER THAN A BUG HERE:
  ///     <c>StateService.AppendAsync</c> reads the row and then delegates to <c>SetAsync</c> with
  ///     the concatenated value, so the bus sees one Set. The fact's job is 'this key moved',
  ///     which is exactly what a reader needs to re-read it - and a view that re-reads does not
  ///     care which verb produced the write. Pinned so the day someone teaches AppendAsync to
  ///     publish its own kind is a deliberate change to this assertion rather than a silent one.
  ///     </para></summary>
  [Fact]
  public async Task StateAppend_PublishesAFactNamingTheKey()
  {
    using ServiceProvider services = Build();
    IAgentEvents events = services.GetRequiredService<IAgentEvents>();
    IStateService state = services.GetRequiredService<IStateService>();
    RecordingSubscriber recorder = new();
    using IDisposable lease = events.Subscribe(recorder);

    Result<StateKeyValue> appended = await state
        .AppendAsync("sci.318-ledger-view/ledger", "prediction 1: y", null,
            TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.True(appended.IsSuccess, appended.Error?.Message ?? "expected success");

    StateChangedEvent change = Assert.Single(recorder.Events.OfType<StateChangedEvent>());
    Assert.Equal("sci.318-ledger-view/ledger", change.Key);
    Assert.Equal("set", change.Change);
  }

  /// <summary>The read port the ledger view is built over is registered and reads the ledgers the
  ///     experiment tool writes - the two sides agree on the key shape, which is the whole point
  ///     of <see cref="ExperimentLedgerKey"/>.</summary>
  [Fact]
  public async Task TheReadPort_ListsWhatTheExperimentToolWrites()
  {
    using ServiceProvider services = Build();
    IExperimentLedgerStore ledger = services.GetRequiredService<IExperimentLedgerStore>();
    IExperimentLedgerAccess access = services.GetRequiredService<IExperimentLedgerAccess>();
    Result<int> appended = await ledger
        .AppendAsync(ExperimentLedgerKeyFor("318-ledger-view"), "hypothesis 1: x", null,
            TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.True(appended.IsSuccess, appended.Error?.Message ?? "expected success");

    Result<IReadOnlyList<LedgerSummary>> listed = await access
        .ListAsync(TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.True(listed.IsSuccess, listed.Error?.Message ?? "expected success");
    LedgerSummary summary = Assert.Single(listed.Value);
    Assert.Equal("318-ledger-view", summary.Slug);
    Assert.Equal(1, summary.RecordCount);
    // A lone hypothesis opens nothing: the open count is about PREDICTIONS.
    Assert.Equal(0, summary.OpenPredictions);
  }

  /// <summary>A ledger nobody has written reads as EMPTY, not as a failure: the experiment tool
  ///     creates the key on the first append, so 'not found' is the ordinary state of a plan
  ///     whose loop has not started.</summary>
  [Fact]
  public async Task ReadingAnUnwrittenLedger_YieldsEmpty_NotAnError()
  {
    using ServiceProvider services = Build();
    IExperimentLedgerAccess access = services.GetRequiredService<IExperimentLedgerAccess>();

    Result<ExperimentLedgerSnapshot> read = await access
        .ReadAsync(ExperimentLedgerKeyFor("never-started"), TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.True(read.IsSuccess, read.Error?.Message ?? "expected success");
    Assert.Equal(0, read.Value.RecordCount);
    Assert.Empty(read.Value.Lines);
  }

  /// <summary>The ledger key for a slug, built through the ONE place that defines its shape.
  ///     </summary>
  /// <param name="slug">The plan slug.</param>
  private static string ExperimentLedgerKeyFor(string slug) =>
      $"{ExperimentLedgerKey.NamespacePrefix}{slug}/{ExperimentLedgerKey.Name}";

  private sealed class RecordingSubscriber : IAgentEventSubscriber
  {
    public List<ChildEvent> Events { get; } = [];

    public void OnEvent(ChildEvent evt) => Events.Add(evt);
  }
}
