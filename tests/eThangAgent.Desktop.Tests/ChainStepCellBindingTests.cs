using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.AgentInfrastructure;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;
using eThangAgent.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>Issue #329, review round 3: the two things that broke when a chain step stopped
///     being the tab's primary cell.
///     <para>ROOT SUPERVISION READS THE SESSION IT SUPERVISES. The watchdog's clock is a
///     view-model's own turn facts, and each view-model holds ONE sink. Binding the tab's
///     primary instead watches the agent that started the chain and clobbers the previous
///     step's clock on every advance - which reads as idleness and nudges or parks a chain on
///     a clock that stopped.</para>
///     <para>THE #322 WAIT COVERS THE SESSION BEING RETIRED. A launch disposes the
///     predecessor's container, so the turn it must wait for is the predecessor's, never the
///     primary's. With the primary idle the wait would return at once and the teardown would
///     dispose a container with a turn still running through it - the
///     <c>Cannot access a disposed object</c> failure #322 exists to prevent.</para>
///     <para>WHY EVERY TEST RUNS TWO ADVANCES: the defect only exists once a step occupies a
///     cell of its own, so a predecessor that IS the primary cannot tell the two receivers
///     apart. The second advance's predecessor is the first step's cell and the primary is
///     idle throughout - which is exactly the shape that silently passed before.</para>
///     <para>WHY THESE DRIVE THE REAL SHELL: both defects are a WRONG RECEIVER, not a missing
///     call. A test that wired the subscriber by hand would pick the receiver itself and pass
///     with the shell binding the primary.</para></summary>
public class ChainStepCellBindingTests
{
  private const string ChainTitle = "Round three";

  private const string WorkspaceRoot = @"C:\work\chain";

  private const string WorkspaceId = "ws:" + WorkspaceRoot;

  /// <summary>How long a step's launch or turn may take before the test calls it wedged.
  ///     Generous: the launch builds a container and the turn crosses a fake host.</summary>
  private static readonly TimeSpan Limit = TestCeiling.Value;

  /// <summary>A three-step chain whose earlier steps are already claimed: step 1 by
  ///     <paramref name="step1SessionId"/>, and step 2 by <paramref name="step2SessionId"/>
  ///     once there is one. The next unclaimed step is the one an advance starts.</summary>
  private static Chain ThreeSteps(string step1SessionId, string? step2SessionId = null)
  {
    DateTimeOffset now = DateTimeOffset.UtcNow;
    return new Chain(1, WorkspaceId, PlanId: 1, PlanVersion: 1, ChainTitle,
        ChainStatus.Running, ParkReason: null, ParkedAtPosition: null, SessionId: null,
        now, now, Version: 1,
        [
          new ChainStep(1, ChainStepStatus.Running, step1SessionId, Attempts: 1,
              Outcome: null, ChainActor.Harness, now, CompletedAt: null, ChainStepRole.Implement),
          step2SessionId is { } second
              ? new ChainStep(2, ChainStepStatus.Running, second, Attempts: 1, Outcome: null,
                  ChainActor.Harness, now, CompletedAt: null, ChainStepRole.Implement)
              : ChainStep.Pending(2),
          ChainStep.Pending(3),
        ]);
  }

  /// <summary>The chain store every created session's container resolves: one chain, answered
  ///     by id and by session, so the advance can read the step it is starting.</summary>
  private sealed class ChainStore : IChainStore
  {
    public Chain? Chain { get; set; }

    public Task<Result<Chain>> CreateAsync(Chain draft, CancellationToken ct = default)
        => Task.FromResult(Result.Success(draft));

    public Task<Result<Chain>> GetAsync(int id, CancellationToken ct = default)
        => Task.FromResult(Chain is { } found && found.Id == id
            ? Result.Success(found)
            : Result.Failure<Chain>(new DomainError("ChainNotFound", "not found")));

    public Task<Result<ChainCatalog>> ListAsync(ChainStatus? status, CancellationToken ct = default)
        => Task.FromResult(Result.Success(new ChainCatalog([], [])));

    public Task<Result<Chain>> SaveAsync(Chain chain, int expectedVersion, CancellationToken ct = default)
    {
      Chain = chain;
      return Task.FromResult(Result.Success(chain));
    }

    public Task<Result<Chain?>> FindBySessionAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Success<Chain?>(
            Chain is { } found && found.Steps.Any(s => s.SessionId == sessionId) ? found : null));
  }

  /// <summary>The brief's facts: enough for a startable brief at any of the three positions,
  ///     so a launch is refused for nothing but the defect under test.</summary>
  private sealed class BriefSource(ChainStore chains) : IChainBriefSource
  {
    public Task<Result<ChainBriefChain>> GetChainAsync(int chainId, int position,
        CancellationToken ct = default)
        => Task.FromResult(chains.Chain is { } chain
            ? Result.Success(new ChainBriefChain(chain.Id, chain.Title, chain.PlanId, [],
                ChainStepRole.Implement))
            : Result.Failure<ChainBriefChain>(new DomainError("ChainNotFound", "not found")));

    public Task<Result<ChainBriefPlan>> GetPlanAsync(int planId, CancellationToken ct = default)
        => Task.FromResult(Result.Success(new ChainBriefPlan(planId, Version: 1, "prove it",
            [new ChainBriefPlanStep(1, "first step", null),
             new ChainBriefPlanStep(2, "second step", null),
             new ChainBriefPlanStep(3, "third step", null)])));

    public Task<Result<bool>> GetReviewRubricAvailableAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success(false));

  }

  /// <summary>The audit sink root supervision writes to; nothing here asserts on it.</summary>
  private sealed class RecordingEvents : IWatchdogEventStore
  {
    public Task<Result<string>> AppendAsync(WatchdogEvent evt, CancellationToken ct = default)
        => Task.FromResult(Result.Success("appended"));

    public Task<Result<IReadOnlyList<WatchdogEvent>>> ListRecentAsync(int limit,
        CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<WatchdogEvent>>([]));

    public Task<Result<IReadOnlyList<WatchdogEvent>>> ListRecentByKindAsync(
        WatchdogEventKind kind, int limit, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<WatchdogEvent>>([]));

    public Task<Result<int>> CountKindForAgentAsync(AgentId agentId, WatchdogEventKind kind,
        CancellationToken ct = default)
        => Task.FromResult(Result.Success(0));
  }

  /// <summary>The composed shell: every created session's container resolves the chain store,
  ///     the brief machinery, an event stream, a watchdog audit sink, and a host channel.
  ///     <para>A turn settles at once unless the test HOLDS IT OPEN, which is the only way to
  ///     tell which session an advance waits for.</para></summary>
  private sealed class Shell
  {
    public ChainStore Chains { get; } = new();

    public RecordingEvents WatchdogEvents { get; } = new();

    public WatchdogLoop Loop { get; } = new(TimeSpan.FromMinutes(1), TimeProvider.System);

    /// <summary>Every host channel the shell's containers resolved.</summary>
    public List<FakeRootTurnChannel> Channels { get; } = [];

    /// <summary>The checkout each launched session was told to work in, in launch order: null
    ///     is the workspace itself, which is every step but a group member's.</summary>
    public List<string?> Anchors { get; } = [];

    /// <summary>Whether a turn holds open until it is stopped. A FLAG, not a pass over the
    ///     channels already built: a step's container - and so its channel - is created by the
    ///     launch, which happens AFTER the test decides to hold turns open.</summary>
    public bool HoldTurnsOpen { get; set; }

    public MainViewModel ViewModel { get; }

    public Shell()
    {
      ViewModel = new MainViewModel(
          (root, provider) => Create(root, provider, seed: null, contract: null),
          new MainViewModelOptions
          {
            UiMarshal = action => action(),
            // THE STREAM SINK MUST NOT POST: the production default marshals every event onto
            // the Avalonia dispatcher, and a headless test has no loop to run those posts - so
            // every turn would wedge inside its own drain. These tests never read a transcript,
            // so the events are dropped.
            UiStreamSink = _ => Task.CompletedTask,
            SeededSessionCreator = (root, provider, seed, contract, anchorRoot) =>
                Create(root, provider, seed, contract, anchorRoot),
            // ROOT SUPERVISION IS WIRED, which is what makes the sink assertion mean
            // something: with no loop there is no watchdog and no sink at all.
            WatchdogLoop = Loop,
            WatchdogPolicy = new WatchdogPolicy(TimeSpan.FromMinutes(15),
                TimeSpan.FromSeconds(60), MaxWrapUpAttempts: 1),
          });
    }

    private FakeRootTurnChannel ResolveChannel(IServiceProvider services)
    {
      _ = services;
      FakeRootTurnChannel channel = new(["ack"], HoldTurnsOpen);
      Channels.Add(channel);
      return channel;
    }

    private Task<Result<AgentSession>> Create(string root, string provider,
        IReadOnlyList<Message>? seed, ChainContract? contract, string? anchorRoot = null)
    {
      _ = seed;
      Anchors.Add(anchorRoot);
      ServiceProvider services = new ServiceCollection()
          .AddSingleton<IAgentEvents>(new InProcessAgentEvents())
          .AddSingleton<IAgentStore>(new TestFixtures.StubStore())
          .AddSingleton<IWatchdogEventStore>(WatchdogEvents)
          // THE SERVICE TYPE IS NAMED EXPLICITLY: a bare AddSingleton(ResolveChannel) registers
          // the concrete channel, and the session would resolve no IRootTurnChannel at all -
          // which runs every turn in process and settles it immediately.
          .AddSingleton<IRootTurnChannel>(ResolveChannel)
          .AddSingleton<IChainStore>(Chains)
          .AddSingleton<IChainBriefSource>(new BriefSource(Chains))
          .AddSingleton(sp => new ChainBriefService(sp.GetRequiredService<IChainBriefSource>()))
          .AddSingleton<ChainContractCarrier>()
          .AddSingleton<IWorkspaceContext>(new FixedWorkspaceContext(WorkspaceId, root))
          .BuildServiceProvider();
      // THE CONTRACT IS WHAT MAKES A SESSION SUPERVISABLE, and the factory fills it at
      // container build exactly as production does.
      if (contract is not null)
      {
        services.GetRequiredService<ChainContractCarrier>().Fill(contract);
      }

      AgentSession session = new(
          services,
          AgentId.NewId(),
          new Conversation(),
          Handler: null!,
          Lifecycle: new RootSessionLifecycle(new TestFixtures.StubStore()),
          Model: ModelConfig.Create("test/model", 128, 0.1f, 8192).Value!,
          WorkspaceRoot: root,
          ProviderName: provider,
          Inbox: new BoundedAgentMailbox(),
          ChildRuntime: new TestFixtures.StubAgentRuntime(),
          Preferences: null);
      return Task.FromResult(Result.Success(session));
    }

    public async Task<AgentTabViewModel> OpenAsync()
      => (await ViewModel.OpenAgentAsync(WorkspaceRoot, "openrouter").ConfigureAwait(true)).Value!;
  }

  /// <summary>Starts <paramref name="position"/> in the shell's only tab, retiring
  ///     <paramref name="predecessor"/>, and returns the cell the new session landed in.
  ///     <para>THE STEP'S KICKOFF TURN IS STILL RUNNING WHEN THIS RETURNS, deliberately: a
  ///     launch fires its first turn without awaiting it, and in production the completion fact
  ///     that asks for the next step is published from INSIDE that turn. A test that settled the
  ///     kickoff first would be testing a shape the app never produces.</para></summary>
  private static async Task<AgentCellViewModel> StartStepAsync(Shell shell, AgentTabViewModel tab,
      AgentId predecessor, int position)
  {
    Result<string> started = await shell.ViewModel.StartChainStepSessionAsync(
        new ChainStepAssignment(1, 1, position, null), predecessor.ToString()).ConfigureAwait(true);
    Assert.True(started.IsSuccess, started.Error?.Message);
    return tab.FindCell(new AgentId(Guid.Parse(started.Value)))
        ?? throw new InvalidOperationException("the started session has no cell in the tab.");
  }

  /// <summary>REGRESSION A: the root-activity sink is bound to the SESSION's own view-model,
  ///     and a later advance does not take it away.
  ///     <para>Three facts, each of which the primary binding breaks. The step's own view-model
  ///     receives a sink (it did not before - the primary did), the primary's stays null
  ///     (binding through the tab would have written it), and step 2's sink is still the SAME
  ///     delegate after step 3 launches - the clobber that silences an earlier step's clock.
  ///     </para></summary>
  [Fact]
  public async Task AChainStepsRootActivitySink_IsBoundToItsOwnViewModel_AndSurvivesALaterStep()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync().ConfigureAwait(true);
    AgentId primaryId = tab.Container.RootId;
    shell.Chains.Chain = ThreeSteps(primaryId.ToString());
    AgentSessionViewModel primary = tab.ViewModel;

    AgentCellViewModel step2 = await StartStepAsync(shell, tab, primaryId, position: 2)
        .ConfigureAwait(true);
    Action<RootTurnActivity>? step2Sink = step2.ViewModel.RootActivityObserver;
    Assert.NotSame(primary, step2.ViewModel);
    Assert.NotNull(step2Sink);
    Assert.Null(primary.RootActivityObserver);

    shell.Chains.Chain = ThreeSteps(primaryId.ToString(), step2.RootId.ToString());
    AgentCellViewModel step3 = await StartStepAsync(shell, tab, step2.RootId, position: 3)
        .ConfigureAwait(true);

    Assert.NotNull(step3.ViewModel.RootActivityObserver);
    Assert.Same(step2Sink, step2.ViewModel.RootActivityObserver);
    Assert.Null(primary.RootActivityObserver);
  }

  /// <summary>A GROUP MEMBER'S SESSION IS ANCHORED AT ITS OWN CHECKOUT.
  ///     <para>THE ANCHOR IS THE WHOLE POINT OF THE WORKTREE: two members writing one checkout
  ///     invalidate each other's completion gates continuously, so a member launched without its
  ///     anchor is a member running in the shared directory the worktree exists to keep it out
  ///     of.</para></summary>
  [Fact]
  public async Task AGroupMembersSessionIsAnchoredAtItsWorktree()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync().ConfigureAwait(true);
    AgentId primaryId = tab.Container.RootId;
    shell.Chains.Chain = ThreeSteps(primaryId.ToString());
    string worktree = Path.Combine(Path.GetTempPath(), "chain-1-step-2");

    Result<string> started = await shell.ViewModel.StartChainStepSessionAsync(
        new ChainStepAssignment(1, 1, 2, null).WithWorktree(worktree), primaryId.ToString())
        .ConfigureAwait(true);

    Assert.True(started.IsSuccess, started.Error?.Message);
    Assert.Equal(worktree, shell.Anchors[^1]);
  }

  /// <summary>A SEQUENTIAL STEP IS ANCHORED AT NOTHING, which is the workspace it already works
  ///     in: the launch passes no anchor, so the session behaves exactly as it did before a group
  ///     existed.</summary>
  [Fact]
  public async Task ASequentialStepsSessionCarriesNoAnchor()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync().ConfigureAwait(true);
    AgentId primaryId = tab.Container.RootId;
    shell.Chains.Chain = ThreeSteps(primaryId.ToString());

    Result<string> started = await shell.ViewModel.StartChainStepSessionAsync(
        new ChainStepAssignment(1, 1, 2, null), primaryId.ToString()).ConfigureAwait(true);

    Assert.True(started.IsSuccess, started.Error?.Message);
    Assert.Null(shell.Anchors[^1]);
  }

  /// <summary>REGRESSION B: the advance waits for the PREDECESSOR's turn, not for whichever
  ///     cell the tab happens to call its own.
  ///     <para>The predecessor is step 2's cell and its turn is held open; the agent it
  ///     superseded is a finished card and idle throughout. A wait addressed anywhere but the
  ///     predecessor would complete while it is still busy - and the teardown would then dispose
  ///     a container with a turn running through it, which is the failure #322 exists to
  ///     prevent.</para>
  ///     <para>ASSERTED AS A PAIR: still pending while the predecessor is busy, and landed once
  ///     that turn ends. Either half alone would pass for the wrong reason.</para>
  ///     <para>WHY THE IDLE CELL IS CELL ZERO AND NOT <c>tab.ViewModel</c> (issue #332): the tab
  ///     resolves 'its session' to its first LIVE cell, and in a two-cell tab that is the
  ///     predecessor itself. The idle cell the wait must not be reading is the finished card the
  ///     predecessor superseded, so that is what this names.</para></summary>
  [Fact]
  public async Task AChainAdvance_WaitsForThePredecessorsTurn_NotThePrimarys()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync().ConfigureAwait(true);
    AgentId primaryId = tab.Container.RootId;
    shell.Chains.Chain = ThreeSteps(primaryId.ToString());
    // EVERY STEP'S KICKOFF TURN HOLDS OPEN, which is what makes the predecessor BUSY: in
    // production the completion fact is published from inside that turn.
    shell.HoldTurnsOpen = true;

    // Step 2 lands beside the primary, which is the shape every later advance is in.
    AgentCellViewModel step2 = await StartStepAsync(shell, tab, primaryId, position: 2)
        .ConfigureAwait(true);
    shell.Chains.Chain = ThreeSteps(primaryId.ToString(), step2.RootId.ToString());

    // THE SUPERSEDED CARD IS IDLE, THE PREDECESSOR IS BUSY: the one arrangement in which a
    // wait addressed at the wrong cell would return at once.
    Assert.False(tab.Cells[0].ViewModel.IsBusy, "the superseded card is idle");
    Assert.True(step2.ViewModel.IsBusy);
    Task<Result<string>> advance = shell.ViewModel.StartChainStepSessionAsync(
        new ChainStepAssignment(1, 1, 3, null), step2.RootId.ToString());
    Task finished = await Task.WhenAny(advance, Task.Delay(TimeSpan.FromMilliseconds(300),
        TestContext.Current.CancellationToken)).ConfigureAwait(true);
    if (ReferenceEquals(finished, advance))
    {
      Result<string> early = await advance.ConfigureAwait(true);
      Assert.Fail("the advance finished while the predecessor's turn was still running: "
          + (early.IsSuccess
              ? "started " + early.Value
              : early.Error?.Code + " - " + early.Error?.Message));
    }

    // END THE TURN: the wait the advance performs must now complete, and the launch land.
    step2.ViewModel.RequestStop();
    await step2.ViewModel.WaitForTurnAsync().WaitAsync(Limit,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Result<string> started = await advance.WaitAsync(Limit,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(started.IsSuccess, started.Error?.Message);
    Assert.NotNull(tab.FindCell(new AgentId(Guid.Parse(started.Value))));
  }
}
