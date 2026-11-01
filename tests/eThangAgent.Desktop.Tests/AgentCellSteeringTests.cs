using eThangAgent.AgentDomain;
using eThangAgent.AgentInfrastructure;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;
using eThangAgent.Transport.ACL;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>Issue #330: a cell's chat input STEERS the agent that runs in its own host, and
///     RESUMES the one that has settled.
///     <para>WHY THE REAL SHELL: the defect class this closes is a MISSING WIRING. A test that
///     built a view-model by hand could pass with the cell never given a steering seam - the
///     pre-#330 shape, where the input was hidden and the turn runner refused every call. Every
///     test here goes through <c>OpenAgentAsync</c> and a started fact, so removing the seam from
///     the shell's cell construction fails them.</para>
///     <para>THE SEAM IS FAKED, THE REST IS NOT: the whole submit path - the routing, the echo,
///     the refusal notice, the finished-state flip, and the store reconciliation - runs for real
///     against a fake that records what it was asked to do. Only the host and the pipe are
///     absent, and those are what the wire tests in Transport.ACL cover.</para></summary>
public class AgentCellSteeringTests
{
  /// <summary>A running agent's cell renders its chat input, and the input is addressed to THAT
  ///     agent: the id the cell shows is the id the steer carries.
  ///     <para>Before #330 this input was hidden, deliberately - an input that silently did
  ///     nothing would have been worse than none. It renders now because it does something.
  ///     </para></summary>
  [Fact]
  public async Task ARunningAgentsCell_RendersItsInput_AndItsSubmitSteersThatAgent()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync("C:\\work\\alpha");
    AgentId agent = shell.StartAgent(tab, "reviewer");
    AgentCellViewModel cell = await AwaitCellAsync(tab, agent);
    Assert.True(cell.ViewModel.ShowsChatInput);

    await cell.ViewModel.SubmitAsync("check the second test").ConfigureAwait(true);

    (AgentId steered, string text, Guid id) = Assert.Single(shell.Steering.Steers);
    Assert.Equal(agent, steered);
    Assert.Equal("check the second test", text);
    Assert.NotEqual(Guid.Empty, id);
  }

  /// <summary>THE MESSAGE RENDERS EXACTLY ONCE. The submit echoes it locally on acceptance and
  ///     the agent loop persists the same message when it drains it, so the poll reads a copy the
  ///     cell has already shown. The reconciliation must release the echo instead of appending
  ///     the copy - two entries for one message is the double echo this pins.</summary>
  [Fact]
  public async Task ASteeredMessage_RendersExactlyOnce_WhenTheStoreCopyArrives()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync("C:\\work\\beta");
    AgentId agent = shell.StartAgent(tab, "tester");
    AgentCellViewModel cell = await AwaitCellAsync(tab, agent);
    DateTimeOffset at = DateTimeOffset.UtcNow;

    await cell.ViewModel.SubmitAsync("look at the diff").ConfigureAwait(true);
    // The agent loop drains the mailbox and persists what it consumed.
    await cell.ViewModel.AppendPersistedMessagesAsync(
        [new Message(Role.User, "look at the diff", at)]).ConfigureAwait(true);

    UserMessageEntry only = Assert.Single(cell.ViewModel.Transcript.Entries.OfType<UserMessageEntry>());
    Assert.Equal("look at the diff", only.Text);
    Assert.False(only.Pending, "the store holds it, so the model has seen it");
  }

  /// <summary>A REFUSED steer fails VISIBLY in the cell and renders no echo: the message was not
  ///     queued, so claiming it was would be the silent swallow this issue exists to remove.
  ///     <para>The reason travels: the notice names the host's own code, so a closed mailbox and
  ///     a host that never answered are distinguishable to the person reading it.</para></summary>
  [Fact]
  public async Task ARefusedSteer_RendersATypedNotice_AndNoEcho()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync("C:\\work\\gamma");
    AgentId agent = shell.StartAgent(tab, "reviewer");
    AgentCellViewModel cell = await AwaitCellAsync(tab, agent);
    shell.Steering.Refusal = new DomainError("NotRunning", "the agent has no live run in its host.");

    await cell.ViewModel.SubmitAsync("are you still there?").ConfigureAwait(true);

    Assert.Contains(cell.ViewModel.Transcript.Notices,
        notice => notice.Contains("Error [NotRunning]", StringComparison.Ordinal));
    Assert.Empty(cell.ViewModel.Transcript.Entries.OfType<UserMessageEntry>());
  }

  /// <summary>A SETTLED agent's cell shows an input whose submit RESUMES it: the cell leaves its
  ///     finished state and follows the new run, and the text it was given is the continuation
  ///     prompt.
  ///     <para>RESUME MEANS RELAUNCH: a settled agent's host is gone, so there is nothing live to
  ///     steer. The submit reaches the runtime that relaunches one for the record - the act
  ///     agent.resume performs, and the one a parked chain step needs.</para></summary>
  [Fact]
  public async Task ASettledAgentsCell_ResumesIt_AndFollowsTheNewRun()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync("C:\\work\\delta");
    AgentId agent = shell.StartAgent(tab, "reviewer");
    AgentCellViewModel cell = await AwaitCellAsync(tab, agent);
    shell.Settle(agent);
    await WaitUntilAsync(() => cell.IsFinished);
    Assert.True(cell.ViewModel.IsSettled);

    await cell.ViewModel.SubmitAsync("finish the review").ConfigureAwait(true);

    (AgentId resumed, string text) = Assert.Single(shell.Steering.Resumes);
    Assert.Equal(agent, resumed);
    Assert.Equal("finish the review", text);
    Assert.False(cell.IsFinished, "a resumed agent is running again, so the card stops being finished");
    Assert.False(cell.ViewModel.IsSettled);
    Assert.Empty(shell.Steering.Steers);
  }

  /// <summary>The resumed run persists the text it was given as its prompt, so the store copy is
  ///     the same message the cell already shows - one entry, not two.
  ///     <para>The cursor the poll resumes from is advanced past the local entry, so the copy
  ///     cannot be appended beside it.</para></summary>
  [Fact]
  public async Task AResumedAgentsPrompt_RendersExactlyOnce()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync("C:\\work\\epsilon");
    AgentId agent = shell.StartAgent(tab, "tester");
    AgentCellViewModel cell = await AwaitCellAsync(tab, agent);
    shell.Settle(agent);
    await WaitUntilAsync(() => cell.IsFinished);

    await cell.ViewModel.SubmitAsync("carry on").ConfigureAwait(true);
    await cell.ViewModel.AppendPersistedMessagesAsync(
        [new Message(Role.User, "carry on", DateTimeOffset.UtcNow)]).ConfigureAwait(true);

    _ = Assert.Single(cell.ViewModel.Transcript.Entries.OfType<UserMessageEntry>());
  }

  /// <summary>A cell whose container wired no host channel has NO steering, and says so: an input
  ///     that silently did nothing is the shape this issue refuses, so the refusal is a notice
  ///     naming why the message went nowhere.
  ///     <para>Such a container is a headless fixture - production always wires the channel - but
  ///     the honest answer is the observable contract either way.</para></summary>
  [Fact]
  public async Task ACellWithNoSteeringSeam_ReportsItCannotReachItsAgent()
  {
    // The container is built at tab open, so the seam's absence is decided BEFORE it exists.
    Shell shell = new() { WireChannel = false };
    AgentTabViewModel tab = await shell.OpenAsync("C:\\work\\zeta");
    AgentId agent = shell.StartAgent(tab, "reviewer");
    AgentCellViewModel cell = await AwaitCellAsync(tab, agent);
    Assert.True(cell.ViewModel.ShowsChatInput, "the input renders; its refusal is what must be visible");

    await cell.ViewModel.SubmitAsync("anyone home?").ConfigureAwait(true);

    Assert.Contains(cell.ViewModel.Transcript.Notices,
        notice => notice.Contains("Error [NoSteering]", StringComparison.Ordinal));
  }

  /// <summary>A card whose container the shell retired hides its input: there is no session
  ///     behind it to serve a turn, and a retired card must not offer an affordance it cannot
  ///     honor.
  ///     <para>A HOST cell is the opposite case and keeps its input - its agent lives in another
  ///     process and can still be steered or resumed - which is why the view-model is told
  ///     explicitly rather than inferring it from a null container.</para></summary>
  [Fact]
  public async Task ARetiredContainerCellsInput_IsHidden_WhileAHostCellsStays()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync("C:\\work\\eta");
    AgentId agent = shell.StartAgent(tab, "reviewer");
    AgentCellViewModel hostCell = await AwaitCellAsync(tab, agent);
    tab.Cells[0].RetireContainer();

    Assert.False(tab.Cells[0].ViewModel.ShowsChatInput);
    Assert.True(hostCell.ViewModel.ShowsChatInput);
  }

  /// <summary>The composed shell, a fake steering seam, and the streams and stores the shell
  ///     resolves.</summary>
  private sealed class Shell
  {
    public InProcessAgentEvents Events { get; } = new();

    public Store Store { get; } = new();

    public Chains Chains { get; } = new();

    public FakeSteering Steering { get; } = new();

    public MainViewModel ViewModel { get; }

    /// <summary>Whether the opened session's container registers the host channel a cell's
    ///     steering is built from. False drives the 'cannot reach the agent' contract.</summary>
    public bool WireChannel { get; set; } = true;

    public Shell()
    {
      ViewModel = new MainViewModel(
          (root, provider) => Task.FromResult(Result.Success(Session(root))),
          new MainViewModelOptions
          {
            // Synchronous: a posted cell mutation would never run without a dispatcher loop.
            UiMarshal = action => action(),
          });
    }

    public static string WorkspaceId(string root) => "ws:" + root;

    public async Task<AgentTabViewModel> OpenAsync(string root)
    {
      Result<AgentTabViewModel> opened = await ViewModel.OpenAgentAsync(root, "openrouter").ConfigureAwait(true);
      Assert.True(opened.IsSuccess, opened.Error?.Message);
      return opened.Value;
    }

    /// <summary>Starts one agent in the tab's workspace: the shell turns the started fact into a
    ///     cell, which is the shape these tests need before a submit means anything.</summary>
    public AgentId StartAgent(AgentTabViewModel tab, string label)
    {
      AgentId agent = AgentId.NewId();
      Store.Records[agent.Value] = AgentRecord.Spawned(agent, parentId: null, depth: 1,
          "test/model", label, "p", DateTimeOffset.UtcNow,
          workspaceId: WorkspaceId(tab.Container.WorkspaceRoot));
      Events.Publish(new ChildStartedEvent(agent, DateTimeOffset.UtcNow, tab.Container.RootId,
          "test/model", 1));
      return agent;
    }

    /// <summary>Publishes the settled fact the shell turns into a finished card.</summary>
    public void Settle(AgentId agent)
        => Events.Publish(new ChildSettledEvent(agent, DateTimeOffset.UtcNow,
            AgentStatus.Completed, null, 12));

    private AgentSession Session(string root)
    {
      IServiceCollection services = new ServiceCollection()
          .AddSingleton<IAgentEvents>(Events)
          .AddSingleton<IAgentStore>(Store)
          .AddSingleton<IChainStore>(Chains)
          .AddSingleton<IWorkspaceContext>(new FixedWorkspaceContext(WorkspaceId(root), root));
      if (WireChannel)
      {
        // The two seams the shell builds a host agent's steering from, exactly as production
        // registers them: the wire channel to the agent's host, and the runtime that relaunches
        // one for a settled record.
        _ = services.AddSingleton<IRootTurnChannel>(Steering)
            .AddSingleton<IAgentRuntime>(Steering);
      }

      ServiceProvider provider = services.BuildServiceProvider();
      return new AgentSession(
          provider,
          AgentId.NewId(),
          new Conversation(),
          Handler: null!,
          Lifecycle: new RootSessionLifecycle(Store),
          Model: ModelConfig.Create("test/model", 128, 0.1f, 8192).Value!,
          WorkspaceRoot: root,
          ProviderName: "openrouter",
          Inbox: new BoundedAgentMailbox(),
          ChildRuntime: new TestFixtures.StubAgentRuntime());
    }
  }

  /// <summary>The faked host seam (issue #330): it records every steer and resume and answers
  ///     with the outcome the test scripted. It is BOTH the channel and the runtime, because
  ///     production resolves them from the same container and the cell treats them as one act.
  ///     </summary>
  private sealed class FakeSteering : IRootTurnChannel, IAgentRuntime
  {
    public List<(AgentId AgentId, string Text, Guid MessageId)> Steers { get; } = [];

    public List<(AgentId AgentId, string Text)> Resumes { get; } = [];

    /// <summary>The refusal every steer receives. Null = the host accepted it.</summary>
    public DomainError? Refusal { get; set; }

    public Task<Result<bool>> SteerAgentAsync(AgentId agentId, string text, Guid messageId)
    {
      Steers.Add((agentId, text, messageId));
      return Task.FromResult(Refusal is { } refusal
          ? Result.Failure<bool>(refusal)
          : Result.Success(true));
    }

    public Task<Result<AgentId>> Resume(AgentId id, string message, CancellationToken ct = default)
    {
      Resumes.Add((id, message));
      return Task.FromResult(Result.Success(id));
    }

    // The root-turn half is not this cell's surface: a cell addresses an AGENT, never a turn.
    public Task<Result<string>> SendRootTurnAsync(RootTurnRequest request, Action<RootTurnEvent> onEvent,
        CancellationToken ct = default)
        => throw new NotSupportedException("a cell never sends a root turn.");

    public void InterruptRootTurn(string turnId)
        => throw new NotSupportedException("a cell never interrupts a root turn.");

    public void SteerRootTurn(string turnId, string text, Guid messageId)
        => throw new NotSupportedException("a cell addresses an agent, never a turn.");

    public void SendSessionResponse(string requestId, string? sessionId, string? errorCode, string? errorMessage)
        => throw new NotSupportedException("a cell never answers a session-request.");

    public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
        => throw new NotSupportedException("a cell never starts an agent.");

    public Task<Result<AgentRunOutcome>> WhenSettledAsync(AgentId id, CancellationToken ct = default)
        => throw new NotSupportedException("a cell never awaits a settle.");

    public void InterruptSubtree(AgentId rootOfSubtree)
        => throw new NotSupportedException("a cell never interrupts a subtree.");

    public void Interrupt(AgentId? childId = null)
        => throw new NotSupportedException("a cell never interrupts an agent.");
  }

  /// <summary>An agent store that answers records and transcripts, so a cell's label, its
  ///     placement, and its transcript are drivable without a database.</summary>
  private sealed class Store : IAgentStore
  {
    public Dictionary<Guid, AgentRecord> Records { get; } = [];

    public Dictionary<Guid, List<Message>> Transcripts { get; } = [];

    public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success("saved"));

    public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success("updated"));

    public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Records.TryGetValue(id.Value, out AgentRecord? record)
            ? Result.Success(record)
            : Result.Failure<AgentRecord>(new DomainError("NotFound", "not found")));

    public Task<Result<string>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default)
        => Task.FromResult(Result.Success("appended"));

    public Task<Result<string>> ReplaceTranscriptAsync(AgentId id, IReadOnlyList<Message> messages,
        CancellationToken ct = default) => Task.FromResult(Result.Success(id.ToString()));

    public Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<Message>>(
            Transcripts.TryGetValue(id.Value, out List<Message>? messages) ? messages : []));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId,
        CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>(
            [.. Records.Values.Where(record => record.ParentId == parentId)]));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([.. Records.Values]));
  }

  /// <summary>A chain store with nothing in it: these tests start no chain, so no agent states a
  ///     placement. Every write is refused, which a test that strayed into one would see.
  ///     </summary>
  private sealed class Chains : IChainStore
  {
    public Task<Result<Chain>> CreateAsync(Chain draft, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<Chain>(new DomainError("ChainNotFound", "no chains here")));

    public Task<Result<Chain>> GetAsync(int id, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<Chain>(new DomainError("ChainNotFound", "no chains here")));

    public Task<Result<ChainCatalog>> ListAsync(ChainStatus? status, CancellationToken ct = default)
        => Task.FromResult(Result.Success(new ChainCatalog([], [])));

    public Task<Result<Chain>> SaveAsync(Chain chain, int expectedVersion, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<Chain>(new DomainError("ChainNotFound", "no chains here")));

    public Task<Result<Chain?>> FindBySessionAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Success<Chain?>(null));
  }

  /// <summary>Waits for the cell showing <paramref name="agentId"/> to appear. The cell is
  ///     appended from an async record read, so the wait is on the observable outcome rather than
  ///     on a fixed delay.</summary>
  private static async Task<AgentCellViewModel> AwaitCellAsync(AgentTabViewModel tab, AgentId agentId)
  {
    await WaitUntilAsync(() => tab.FindCell(agentId) is not null).ConfigureAwait(true);
    return tab.FindCell(agentId)!;
  }

  /// <summary>Waits on an observable condition with a real bound, so a regression fails with a
  ///     message instead of hanging the suite.</summary>
  private static async Task WaitUntilAsync(Func<bool> condition)
  {
    DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
    while (!condition())
    {
      if (DateTimeOffset.UtcNow > deadline)
      {
        Assert.Fail("condition not met within 10 seconds");
      }

      await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken)
          .ConfigureAwait(true);
    }
  }
}
