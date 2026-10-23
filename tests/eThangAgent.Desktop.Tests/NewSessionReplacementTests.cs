using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>The shell's in-place session replacement (issue #234 task 7): starting a new
///     session in a tab REPLACES that tab's session rather than opening a second tab — the
///     transcript clears, the status bar names a new session id, and the old session closes
///     gracefully so the Sessions dialog can still resume it.
/// <para>Two entry points share one core: the user's /new command
///     (<see cref="MainViewModel.StartNewSessionAsync"/>) and the host's session-request
///     (the new_session tool or a compaction handoff,
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
        Model: ModelConfig.Create("test/model", null, 128, 0.1f, 8192).Value!,
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
    public List<(string Root, string Provider, IReadOnlyList<Message>? Seed)> Created { get; } = [];

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
            SeededSessionCreator = (root, provider, seed) => Create(root, provider, seed),
          });
    }

    private Task<Result<AgentSession>> Create(string root, string provider, IReadOnlyList<Message>? seed)
    {
      Created.Add((root, provider, seed));
      return Task.FromResult(FailFromCall is { } from && Created.Count > from
          ? Result.Failure<AgentSession>(new DomainError("HostUnavailable", "no host"))
          : Result.Success(FakeSession(root, provider, Host)));
    }

    public async Task<AgentTabViewModel> OpenAsync(string root, string provider = "openrouter")
      => (await ViewModel.OpenAgentAsync(root, provider).ConfigureAwait(true)).Value!;
  }

  [Fact]
  public async Task StartNewSession_ReplacesTheTabInPlace_AtTheSameIndex()
  {
    Shell shell = new();
    AgentTabViewModel alpha = await shell.OpenAsync(@"C:\work\alpha");
    AgentTabViewModel beta = await shell.OpenAsync(@"C:\work\beta");

    Result<AgentTabViewModel> started = await shell.ViewModel.StartNewSessionAsync(beta, "keep going");

    Assert.True(started.IsSuccess, started.Error?.Message);
    // ONE tab replaced, not a third added: the strip is the same length and the new tab
    // sits where the old one did.
    Assert.Equal(2, shell.ViewModel.Tabs.Count);
    Assert.Same(alpha, shell.ViewModel.Tabs[0]);
    Assert.Same(started.Value, shell.ViewModel.Tabs[1]);
    Assert.DoesNotContain(beta, shell.ViewModel.Tabs);
    Assert.Same(started.Value, shell.ViewModel.SelectedTab);
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
    (string _, string _, IReadOnlyList<Message>? seed) = shell.Created[^1];
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

    (string _, string _, IReadOnlyList<Message>? seed) = shell.Created[^1];
    Assert.Null(seed);
  }

  [Fact]
  public async Task SlashNewCommand_ReachesTheShell_AndReplacesTheTab()
  {
    // The user-facing path end to end: /new <prompt> resolves BEFORE the skill lookup
    // (issue #234) and reaches the shell's replacement, which fires the prompt as the new
    // session's first turn.
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha").ConfigureAwait(true);

    await tab.ViewModel.SubmitAsync("/new explore the auth module").ConfigureAwait(true);

    Assert.Equal(2, shell.Created.Count);
    AgentTabViewModel fresh = Assert.Single(shell.ViewModel.Tabs);
    Assert.NotSame(tab, fresh);
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
