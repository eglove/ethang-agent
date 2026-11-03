using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>Starting a new session in a tab APPENDS a cell (issue #332): the new agent gets its
///     own card, the agent it supersedes keeps its card and its transcript, and the old session
///     closes gracefully so the Sessions dialog can still resume it.
/// <para>Two entry points share one core: the user's /new command
///     (<see cref="MainViewModel.StartNewSessionAsync(AgentTabViewModel, string, string)"/>) and the host's session-request
///     (a compaction handoff or a chain launch,
///     <see cref="MainViewModel.HandleSessionRequestAsync"/>). The host BLOCKS on the answer,
///     so every request must be answered exactly once — with the new session's id, or with a
///     typed refusal.</para>
/// <para>Session creation is faked at the factory seam; the real factory's seeded create is
///     covered in Composition tests.</para></summary>
public class NewSessionReplacementTests
{
  /// <summary>A session whose container is never disposed (no ServiceProvider) — tests that
  ///     replace tabs must not touch Services. Built via a throwaway ServiceCollection so
  ///     DisposeAsync stays legal.</summary>
  private static AgentSession FakeSession(string root, string provider = "openrouter",
      IRootTurnChannel? channel = null)
  {
    IServiceCollection registrations = new ServiceCollection()
        .AddSingleton<IAgentStore>(new TestFixtures.StubStore());
    if (channel is not null)
    {
      _ = registrations.AddSingleton(channel);
    }

    ServiceProvider services = registrations.BuildServiceProvider();
    return new AgentSession(
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
  }

  /// <summary>The shell under test plus what it was asked to create: every creation call is
  ///     recorded so a test can assert the seed and the (workspace, provider) pair that were
  ///     actually used. <see cref="FailFromCall"/> makes creation fail from that call onward
  ///     (1-based), which is how a replacement's failure is exercised without a second shell.</summary>
  private sealed class Shell
  {
    public List<(string Root, string Provider, IReadOnlyList<Message>? Seed, ChainContract? Contract)> Created { get; } = [];

    /// <summary>Creation calls from this index onward (0-based) fail.</summary>
    public int? FailFromCall { get; set; }

    /// <summary>The host every created session talks to, so a fired first turn streams from
    ///     a fake host instead of reaching the (absent) in-process handler.</summary>
    public FakeRootTurnChannel Host { get; } = new(["ok"], holdOpen: false);

    public MainViewModel ViewModel { get; }

    public Shell()
    {
      ViewModel = new MainViewModel(
          (root, provider) => Create(root, provider, seed: null),
          new MainViewModelOptions
          {
            SeededSessionCreator = (root, provider, seed, chainContract) =>
                Create(root, provider, seed, chainContract),
          });
    }

    private Task<Result<AgentSession>> Create(string root, string provider, IReadOnlyList<Message>? seed,
        ChainContract? chainContract = null)
    {
      Created.Add((root, provider, seed, chainContract));
      return Task.FromResult(FailFromCall is { } from && Created.Count > from
          ? Result.Failure<AgentSession>(new DomainError("HostUnavailable", "no host"))
          : Result.Success(FakeSession(root, provider, Host)));
    }

    public async Task<AgentTabViewModel> OpenAsync(string root, string provider = "openrouter")
      => (await ViewModel.OpenAgentAsync(root, provider).ConfigureAwait(true)).Value!;
  }

  /// <summary>ISSUE #332 CHANGED THIS CONTRACT. #307's grid appended a cell for a chain step
  ///     but still SWAPPED the primary cell for a handoff and for `/new`, so a tab that
  ///     restarted its conversation lost the one it had. Every route appends now: the old
  ///     agent keeps its card as a finished record, and the new one arrives beside it.
  ///     <para>WHAT DOES NOT CHANGE: the STRIP. Two tabs stay two tabs, in order, and the
  ///     selection does not move - a new cell never steals the view (D2).</para></summary>
  [Fact]
  public async Task StartNewSession_AppendsACell_AndTheTabSurvives()
  {
    Shell shell = new();
    AgentTabViewModel alpha = await shell.OpenAsync(@"C:\work\alpha");
    AgentTabViewModel beta = await shell.OpenAsync(@"C:\work\beta");
    AgentId retired = beta.Container.RootId;
    AgentCellViewModel original = Assert.Single(beta.Cells);

    Result<AgentTabViewModel> started = await shell.ViewModel.StartNewSessionAsync(beta, "keep going");

    Assert.True(started.IsSuccess, started.Error?.Message);
    // The STRIP is unchanged: two tabs, in the same order, and the SAME tab object.
    Assert.Equal(2, shell.ViewModel.Tabs.Count);
    Assert.Same(alpha, shell.ViewModel.Tabs[0]);
    Assert.Same(beta, shell.ViewModel.Tabs[1]);
    Assert.Same(beta, started.Value);
    Assert.Same(beta, shell.ViewModel.SelectedTab);
    // THE CARD THE USER WAS READING IS STILL THERE, in cell zero, as a finished record.
    Assert.Equal(2, beta.Cells.Count);
    Assert.Same(original, beta.Cells[0]);
    Assert.Equal(retired, original.RootId);
    Assert.True(original.IsFinished);
    Assert.Null(original.Container);
    // The new agent is the tab's live session, and it is the LAST cell.
    Assert.NotEqual(retired, beta.Cells[1].RootId);
    Assert.Same(beta.Cells[1].ViewModel, beta.ViewModel);
  }

  [Fact]
  public async Task StartNewSession_NewSessionHasADifferentId_AndTheSameWorkspace()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    AgentId before = tab.Container.RootId;

    Result<AgentTabViewModel> started = await shell.ViewModel.StartNewSessionAsync(tab, "go");

    Assert.True(started.IsSuccess, started.Error?.Message);
    Assert.NotEqual(before, started.Value.Container.RootId);
    Assert.Equal(@"C:\work\alpha", started.Value.Container.WorkspaceRoot, StringComparer.OrdinalIgnoreCase);
    // The status bar names the NEW session — the surface's proof the conversation restarted.
    Assert.Equal(started.Value.Container.RootId.ToString()[..8], started.Value.ViewModel.SessionIdShort);
  }

  /// <summary>A SUPERSEDED card keeps what it ran (issue #332): its transcript survives the
  ///     retire teardown, so the record of the conversation the user was reading is still there.
  ///     <para>THE ENTRIES ARE THE ASSERTION, not a flag: a card that keeps its finished outline
  ///     while rendering an empty transcript is exactly the 'work is unreachable' failure this
  ///     issue exists for, and a flag would have been green through it.</para></summary>
  [Fact]
  public async Task StartNewSession_KeepsTheSupersededCardsTranscript()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    AgentCellViewModel original = Assert.Single(tab.Cells);
    original.ViewModel.Transcript.AddUser("the question I asked the first agent");
    original.ViewModel.Transcript.AppendAssistantDelta("and the answer it gave");

    Result<AgentTabViewModel> started = await shell.ViewModel.StartNewSessionAsync(tab, "go");

    Assert.True(started.IsSuccess, started.Error?.Message);
    Assert.Equal("the question I asked the first agent",
        Assert.Single(original.ViewModel.Transcript.Entries.OfType<UserMessageEntry>()).Text);
    Assert.Contains("and the answer it gave", original.ViewModel.Transcript.AssistantText,
        StringComparison.Ordinal);
  }

  [Fact]
  public async Task StartNewSession_RefusesWhileATurnRuns_AndLeavesTheTabAlone()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    tab.ViewModel.IsBusy = true;

    Result<AgentTabViewModel> started = await shell.ViewModel.StartNewSessionAsync(tab, "go");

    Assert.False(started.IsSuccess);
    Assert.Equal("TurnBusy", started.Error.Code);
    Assert.Same(tab, Assert.Single(shell.ViewModel.Tabs));
    _ = Assert.Single(shell.Created); // only the original open created a session
  }

  [Fact]
  public async Task StartNewSession_ForAClosedTab_IsRefused_AndCreatesNothing()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    await shell.ViewModel.CloseTabAsync(tab);

    Result<AgentTabViewModel> started = await shell.ViewModel.StartNewSessionAsync(tab, "go");

    Assert.False(started.IsSuccess);
    Assert.Equal("TabClosed", started.Error.Code);
    Assert.Empty(shell.ViewModel.Tabs);
    _ = Assert.Single(shell.Created);
  }

  [Fact]
  public async Task StartNewSession_WithAHandoffSummary_SeedsTheNewConversation()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");

    Result<AgentTabViewModel> started =
        await shell.ViewModel.StartNewSessionAsync(tab, prompt: null, handoffSummary: "the dense summary");

    Assert.True(started.IsSuccess, started.Error?.Message);
    (string _, string _, IReadOnlyList<Message>? seed, ChainContract? _) = shell.Created[^1];
    Message seeded = Assert.Single(seed!);
    // A summary message, not user voice: the model must read it as a summary of earlier
    // work, and the prefix must not claim a compaction that never happened.
    Assert.True(seeded.IsSummary);
    Assert.Equal(Role.System, seeded.Role);
    Assert.StartsWith(SessionHandoffSeed.Prefix, seeded.Content, StringComparison.Ordinal);
    Assert.Contains("the dense summary", seeded.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task StartNewSession_WithoutASeededCreator_RefusesAHandoff_RatherThanDroppingIt()
  {
    // A host that supplied only the plain creation delegate has no store to seed. Silently
    // launching a seedless session would lose the summary the handoff exists to carry.
    MainViewModel vm = new((root, provider) =>
        Task.FromResult(Result.Success(FakeSession(root, provider))));
    AgentTabViewModel tab = (await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter")).Value!;

    Result<AgentTabViewModel> started =
        await vm.StartNewSessionAsync(tab, prompt: null, handoffSummary: "the summary");

    Assert.False(started.IsSuccess);
    Assert.Equal("SeedUnsupported", started.Error.Code);
    Assert.Same(tab, Assert.Single(vm.Tabs));
  }

  [Fact]
  public async Task StartNewSession_PlainLaunch_CreatesWithNoSeed()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");

    _ = await shell.ViewModel.StartNewSessionAsync(tab, "a fresh start");

    (string _, string _, IReadOnlyList<Message>? seed, ChainContract? _) = shell.Created[^1];
    Assert.Null(seed);
  }

  [Fact]
  public async Task SlashNewCommand_ReachesTheShell_AndAppendsACell()
  {
    // The user-facing path end to end: /new <prompt> resolves BEFORE the skill lookup
    // (issue #234) and reaches the shell's start, which fires the prompt as the new
    // session's first turn.
    // Issue #332: the CELL is APPENDED and the TAB survives, so the tab object is the same
    // one, the cell the user was in is still there, and a new one arrived beside it.
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha").ConfigureAwait(true);
    AgentId retired = tab.Container.RootId;
    AgentCellViewModel original = Assert.Single(tab.Cells);

    await tab.ViewModel.SubmitAsync("/new explore the auth module").ConfigureAwait(true);

    Assert.Equal(2, shell.Created.Count);
    Assert.Same(tab, Assert.Single(shell.ViewModel.Tabs));
    Assert.Equal(2, tab.Cells.Count);
    Assert.Same(original, tab.Cells[0]);
    Assert.Equal(retired, tab.Cells[0].RootId);
    Assert.NotEqual(retired, tab.Container.RootId);
    // The prompt became the new session's FIRST TURN — the host received it, not the
    // in-process runner.
    Assert.Equal("explore the auth module", shell.Host.LastRequest!.Input, StringComparer.Ordinal);
  }

  [Fact]
  public async Task SlashNewCommand_WithNoPrompt_IsRefusedByTheViewModel_AndCreatesNothing()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha").ConfigureAwait(true);

    await tab.ViewModel.SubmitAsync("/new").ConfigureAwait(true);

    _ = Assert.Single(shell.Created); // only the original open
    Assert.Same(tab, Assert.Single(shell.ViewModel.Tabs));
    Assert.Contains(tab.ViewModel.Transcript.Notices,
        n => n.Contains("/new <prompt>", StringComparison.Ordinal));
  }

  [Fact]
  public async Task StartNewSession_WithAChainContract_PassesItToTheCreation()
  {
    // Issue #311: the contract is prompt text on the session that is being started, so it has
    // to reach the creation call. A shell that dropped it would start a chain step with no
    // idea which step it is, and nothing else would report it.
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    ChainContract contract = new(7, 104, 2, "sci.dashboard-truth/ledger");

    Result<AgentTabViewModel> started = await shell.ViewModel.StartNewSessionAsync(
        tab, prompt: null, seed: null, chainContract: contract);

    Assert.True(started.IsSuccess, started.Error?.Message);
    (string _, string _, IReadOnlyList<Message>? _, ChainContract? passed) = shell.Created[^1];
    Assert.Equal(contract, passed);
  }

  [Fact]
  public async Task StartNewSession_WithAContractButNoSeededCreator_RefusesRatherThanDroppingIt()
  {
    // The same rule the handoff follows: a host that cannot take the extra fact must refuse,
    // not launch a session that silently lacks it.
    MainViewModel vm = new((root, provider) =>
        Task.FromResult(Result.Success(FakeSession(root, provider))));
    AgentTabViewModel tab = (await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter")).Value!;

    Result<AgentTabViewModel> started = await vm.StartNewSessionAsync(tab, prompt: null,
        seed: null, chainContract: new ChainContract(7, 104, 2, "sci.x/ledger"));

    Assert.False(started.IsSuccess);
    Assert.Equal("SeedUnsupported", started.Error.Code);
    Assert.Same(tab, Assert.Single(vm.Tabs));
  }

  [Fact]
  public async Task StartNewSession_FactoryFailure_IsSurfaced_AndTheTabSurvives()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\ok");
    shell.FailFromCall = 1; // the replacement's creation fails; the original open succeeded

    Result<AgentTabViewModel> started = await shell.ViewModel.StartNewSessionAsync(tab, "go");

    Assert.False(started.IsSuccess);
    Assert.Equal("HostUnavailable", started.Error.Code);
    // A failed launch must never cost the user the tab they were reading.
    Assert.Same(tab, Assert.Single(shell.ViewModel.Tabs));
  }
}
