using System.Diagnostics;
using eThangAgent.AgentDomain;
using eThangAgent.AgentInfrastructure;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>Issue #329: an agent's started fact creates its cell, in the tab for its OWN
///     workspace. The event source is #326's - a launched host's forwarded events republish onto
///     the container that launched it - so what is asserted here is the step after that: the
///     shell turns a <see cref="AgentStartedEvent"/> into exactly one cell in the workspace's
///     tab, and a <see cref="AgentSettledEvent"/> into a finished card.
///     <para>WHY THIS DRIVES THE REAL SHELL RATHER THAN THE SUBSCRIBER DIRECTLY: the defect being
///     closed is a MISSING CALLER. A test that built a subscriber by hand would pass with the
///     subscription never attached. Every test here goes through <c>OpenAgentAsync</c> and the
///     session's own stream, so removing the wiring in <c>AttachAgentCells</c> fails them.</para>
///     <para>NO PARENT IS CONSULTED. An agent carries its own workspace (#305 D5), so the tab is
///     resolved from that stamp: <see cref="AStartedEvent_ForAnotherWorkspace_LandsInThatTabsTab"/>
///     proves the resolution is the agent's own workspace and not the stream's session, and
///     <see cref="AStartedEvent_WhoseWorkspaceIsNotOpen_AppendsNoCell"/> proves a cell is never
///     invented for a workspace nobody opened.</para>
///     <para>NO POLLING: cells come from the event. The transcript inside a cell is read from the
///     shared store (parent #325 D4), but the CELL ITSELF is created by the started fact.</para></summary>
public class AgentCellTests
{
  /// <summary>A session whose container carries a real event stream, store, workspace context, and
  ///     chain store, so the shell's own attach path finds them. The turn runner never runs:
  ///     these tests start agents, they do not converse.</summary>
  /// <param name="root">The workspace root the session addresses.</param>
  /// <param name="events">The container's stream, which the shell subscribes.</param>
  /// <param name="store">The store the agent's record and transcript land in.</param>
  /// <param name="chains">The chain store the workspace's chain records live in.</param>
  /// <param name="workspaceId">The stable workspace identity the tab owns.</param>
  internal static AgentSession FakeSession(string root, IAgentEvents events, IAgentStore store,
      IChainStore chains, string workspaceId)
  {
    ServiceProvider services = new ServiceCollection()
        .AddSingleton(events)
        .AddSingleton(store)
        .AddSingleton(chains)
        .AddSingleton<IWorkspaceContext>(new FixedWorkspaceContext(workspaceId, root))
        .BuildServiceProvider();
    return new AgentSession(
        services,
        AgentId.NewId(),
        new Conversation(),
        Handler: null!,
        Lifecycle: new RootSessionLifecycle(store),
        Model: ModelConfig.Create("test/model", 128, 0.1f, 8192).Value!,
        WorkspaceRoot: root,
        ProviderName: "openrouter",
        Inbox: new BoundedAgentMailbox(),
        ChildRuntime: new TestFixtures.StubAgentRuntime());
  }

  /// <summary>A store that answers the agent's record and transcript, so the cell's label, its
  ///     placement, and its transcript are drivable without a database.</summary>
  private sealed class AgentStore : IAgentStore
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
            [.. Records.Values.Where(r => r.ParentId == parentId)]));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([.. Records.Values]));
  }

  /// <summary>A chain store answering one chain, so a cell's placement is drivable: the link is
  ///     the step's own <c>session_id</c>, which is the only place the relationship is recorded.
  ///     </summary>
  private sealed class ChainStore : IChainStore
  {
    /// <summary>The one chain this store answers, or null when the workspace runs none.</summary>
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
        => Task.FromResult(Result.Success(chain));

    public Task<Result<Chain?>> FindBySessionAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Success<Chain?>(
            Chain is { } found && found.Steps.Any(s => s.SessionId == sessionId) ? found : null));
  }

  /// <summary>The composed shell plus the streams and stores it was given.</summary>
  private sealed class Shell
  {
    public InProcessAgentEvents Events { get; } = new();

    public AgentStore Store { get; } = new();

    public ChainStore Chains { get; } = new();

    public MainViewModel ViewModel { get; }

    public Shell()
    {
      ViewModel = new MainViewModel(
          (root, provider) => Task.FromResult(Result.Success(
              FakeSession(root, Events, Store, Chains, WorkspaceId(root)))),
          new MainViewModelOptions
          {
            // Synchronous: a posted cell mutation would never run without a dispatcher loop.
            UiMarshal = action => action(),
          });
    }

    /// <summary>The workspace identity a root is bound to, so a test can name the workspace an
    ///     agent's record carries without reaching into the session.</summary>
    public static string WorkspaceId(string root) => "ws:" + root;

    public async Task<AgentTabViewModel> OpenAsync(string root)
    {
      Result<AgentTabViewModel> opened = await ViewModel.OpenAgentAsync(root, "openrouter").ConfigureAwait(true);
      Assert.True(opened.IsSuccess, opened.Error?.Message);
      return opened.Value;
    }
  }

  /// <summary>Waits for the cell showing <paramref name="agentId"/> to appear. The cell is
  ///     appended from an async record read, so the wait is on the observable outcome rather than
  ///     on a fixed delay.</summary>
  /// <param name="tab">The tab the cell is expected in.</param>
  /// <param name="agentId">The agent's id.</param>
  private static async Task<AgentCellViewModel> AwaitCellAsync(AgentTabViewModel tab, AgentId agentId)
  {
    DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
    while (DateTimeOffset.UtcNow < deadline)
    {
      if (tab.FindCell(agentId) is { } cell)
      {
        return cell;
      }

      await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    Assert.Fail($"no cell appeared for agent {agentId}; the tab holds {tab.Cells.Count} cell(s).");
    throw new UnreachableException();
  }

  /// <summary>The headline: an agent started in a workspace becomes ONE cell in that
  ///     workspace's tab, captioned with the label its record carries (D9).
  ///     <para>THE LABEL IS THE ASSERTION THAT THE RECORD WAS READ: the started event carries no
  ///     label, so a cell showing 'reviewer' proves the cell was built from the agent's own
  ///     record rather than from the event's metadata alone.</para></summary>
  [Fact]
  public async Task AStartedEvent_AppendsOneCell_WithItsLabel()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    AgentId agent = AgentId.NewId();
    shell.Store.Records[agent.Value] = Record(agent, "reviewer", @"C:\work\alpha");

    shell.Events.Publish(new AgentStartedEvent(agent, DateTimeOffset.UtcNow,
        tab.Container.RootId, "test/model", 1));

    AgentCellViewModel cell = await AwaitCellAsync(tab, agent);
    Assert.True(cell.RunsInOwnHost);
    Assert.Equal("reviewer", cell.Title);
    Assert.Equal(agent, cell.RootId);
    Assert.Null(cell.Container);
    Assert.Null(cell.PlacementLine);
  }

  /// <summary>THE TAB IS THE AGENT'S OWN WORKSPACE'S (issue #329). The started fact arrives on one
  ///     workspace's stream, but the agent's record names a DIFFERENT workspace - and the cell
  ///     lands in the tab for the record's workspace, never the stream's.
  ///     <para>THIS IS THE PARENT-FREE RESOLUTION, ASSERTED: the two tabs are open and only the
  ///     agent's own workspace may receive the cell.</para></summary>
  [Fact]
  public async Task AStartedEvent_ForAnotherWorkspace_LandsInThatTabsTab()
  {
    Shell shell = new();
    AgentTabViewModel alpha = await shell.OpenAsync(@"C:\work\alpha");
    AgentTabViewModel beta = await shell.OpenAsync(@"C:\work\beta");
    AgentId agent = AgentId.NewId();
    shell.Store.Records[agent.Value] = Record(agent, "tester", @"C:\work\beta");

    // Published on ALPHA's stream, while the agent belongs to BETA.
    shell.Events.Publish(new AgentStartedEvent(agent, DateTimeOffset.UtcNow,
        alpha.Container.RootId, "test/model", 1));

    _ = await AwaitCellAsync(beta, agent);
    Assert.Null(alpha.FindCell(agent));
  }

  /// <summary>Exactly one cell: a second started fact for the SAME agent is a watchdog retry of
  ///     one agent, not a second agent, so it must not append a duplicate.</summary>
  [Fact]
  public async Task ARepeatedStartedEvent_DoesNotAppendASecondCell()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    AgentId agent = AgentId.NewId();
    shell.Store.Records[agent.Value] = Record(agent, "reviewer", @"C:\work\alpha");
    AgentStartedEvent started = new(agent, DateTimeOffset.UtcNow, tab.Container.RootId, "test/model", 1);

    shell.Events.Publish(started);
    _ = await AwaitCellAsync(tab, agent);
    shell.Events.Publish(started with { Attempts = 2 });
    // The retry's append (if any) would be a second cell; give it a chance to land.
    await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);

    Assert.Equal(2, tab.Cells.Count);
    _ = Assert.Single(tab.Cells, c => c.RootId == agent);
  }

  /// <summary>D13/D2: the primary session stays cell 0 and the started agent appends after it -
  ///     the tab's identity never moves, and no agent takes the first slot.
  ///     <para>D2's 'no steal' half is asserted too: nothing here focuses or selects, because
  ///     there is no code path that could.</para></summary>
  [Fact]
  public async Task AStartedAgent_AppendsAfterThePrimary_AndThePrimaryStaysCellZero()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    AgentCellViewModel primary = tab.Cells[0];
    AgentId first = AgentId.NewId();
    AgentId second = AgentId.NewId();
    shell.Store.Records[first.Value] = Record(first, "one", @"C:\work\alpha");
    shell.Store.Records[second.Value] = Record(second, "two", @"C:\work\alpha");

    shell.Events.Publish(new AgentStartedEvent(first, DateTimeOffset.UtcNow, tab.Container.RootId, "test/model", 1));
    _ = await AwaitCellAsync(tab, first);
    shell.Events.Publish(new AgentStartedEvent(second, DateTimeOffset.UtcNow, tab.Container.RootId, "test/model", 1));
    _ = await AwaitCellAsync(tab, second);

    Assert.Equal(3, tab.Cells.Count);
    Assert.Same(primary, tab.Cells[0]);
    Assert.Equal(first, tab.Cells[1].RootId);
    Assert.Equal(second, tab.Cells[2].RootId);
    Assert.Same(primary.ViewModel, tab.ViewModel);
  }

  /// <summary>A settled fact finishes the cell: the card dims, its cross appears (D6), and the
  ///     agent stops counting as streaming. The card is NOT removed (D7).</summary>
  [Fact]
  public async Task ASettledEvent_FinishesTheCell_WithoutRemovingIt()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    AgentId agent = AgentId.NewId();
    shell.Store.Records[agent.Value] = Record(agent, "reviewer", @"C:\work\alpha");
    shell.Events.Publish(new AgentStartedEvent(agent, DateTimeOffset.UtcNow, tab.Container.RootId, "test/model", 1));
    AgentCellViewModel cell = await AwaitCellAsync(tab, agent);
    Assert.False(cell.IsFinished);
    Assert.True(cell.IsStreaming, "a running agent in its own host streams until it settles");

    shell.Events.Publish(new AgentSettledEvent(agent, DateTimeOffset.UtcNow,
        AgentStatus.Completed, null, 12));

    DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
    while (!cell.IsFinished && DateTimeOffset.UtcNow < deadline)
    {
      await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
    }

    Assert.True(cell.IsFinished);
    Assert.False(cell.IsStreaming);
    Assert.Contains(cell, tab.Cells);
  }

  /// <summary>The agent's transcript renders in its cell (parent #325 D4): the messages the shared
  ///     store holds are appended, so the card shows what the agent actually did.
  ///     <para>THE MECHANISM IS THE STORE, not the event stream: the agent persists in its own
  ///     process, so the store is the only seam that carries message CONTENT.</para></summary>
  [Fact]
  public async Task ACellsTranscript_ComesFromTheSharedStore()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    AgentId agent = AgentId.NewId();
    shell.Store.Records[agent.Value] = Record(agent, "reviewer", @"C:\work\alpha");
    DateTimeOffset at = DateTimeOffset.UtcNow;
    shell.Store.Transcripts[agent.Value] =
        [new Message(Role.User, "review the diff", at), new Message(Role.Assistant, "the diff is correct", at)];

    shell.Events.Publish(new AgentStartedEvent(agent, DateTimeOffset.UtcNow, tab.Container.RootId, "test/model", 1));
    AgentCellViewModel cell = await AwaitCellAsync(tab, agent);

    DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
    while (!cell.ViewModel.Transcript.AssistantText.Contains("the diff is correct", StringComparison.Ordinal)
        && DateTimeOffset.UtcNow < deadline)
    {
      await Task.Delay(TimeSpan.FromMilliseconds(25), TestContext.Current.CancellationToken);
    }

    Assert.Contains("the diff is correct", cell.ViewModel.Transcript.AssistantText, StringComparison.Ordinal);
    // THE CURSOR DOES NOT DOUBLE-APPEND: the same messages are rendered once, however many
    // polls have run over them.
    Assert.Equal(1, cell.ViewModel.Transcript.Entries.Count(e => e is AssistantTextEntry));
  }

  /// <summary>An agent whose workspace has no open tab appends NO cell: a cell can only exist
  ///     inside an open tab, and inventing one would open a workspace the user did not ask for.
  ///     <para>This is the negative half of the resolution: a started fact naming a workspace
  ///     nothing shows produces nothing, so a cell cannot land in an unrelated grid.</para></summary>
  [Fact]
  public async Task AStartedEvent_WhoseWorkspaceIsNotOpen_AppendsNoCell()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    AgentId agent = AgentId.NewId();
    shell.Store.Records[agent.Value] = Record(agent, "orphan", @"C:\work\elsewhere");

    shell.Events.Publish(new AgentStartedEvent(agent, DateTimeOffset.UtcNow,
        AgentId.NewId(), "test/model", 1));
    await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);

    _ = Assert.Single(tab.Cells);
    Assert.DoesNotContain(tab.Cells, c => c.RootId == agent);
  }

  /// <summary>THE CELL STATES ITS CHAIN AND POSITION when it has one (issue #329): the chain's
  ///     title is the caption and 'step 2 of 5' is the line beneath it.
  ///     <para>The link comes from the chain record's own <c>session_id</c>, which nothing on this
  ///     side read before - so this asserts the read, not just the render.</para></summary>
  [Fact]
  public async Task AChainStepCell_StatesItsChainAndPosition()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    AgentId agent = AgentId.NewId();
    shell.Store.Records[agent.Value] = Record(agent, "step two", @"C:\work\alpha");
    shell.Chains.Chain = ChainWith(agent, position: 2, steps: 5);

    shell.Events.Publish(new AgentStartedEvent(agent, DateTimeOffset.UtcNow,
        tab.Container.RootId, "test/model", 1));

    AgentCellViewModel cell = await AwaitCellAsync(tab, agent);
    Assert.Equal("the chain", cell.Title);
    Assert.Equal("step 2 of 5 \u00b7 the chain", cell.PlacementLine);
  }

  /// <summary>A HAND-STARTED AGENT STATES NOTHING EXTRA: no placement line at all, and its own
  ///     label as the caption. The absence is the assertion - an empty line would be a caption
  ///     claiming a chain the agent is not part of.</summary>
  [Fact]
  public async Task AHandStartedAgent_StatesNoChainPlacement()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    AgentId agent = AgentId.NewId();
    shell.Store.Records[agent.Value] = Record(agent, "by hand", @"C:\work\alpha");

    shell.Events.Publish(new AgentStartedEvent(agent, DateTimeOffset.UtcNow,
        tab.Container.RootId, "test/model", 1));

    AgentCellViewModel cell = await AwaitCellAsync(tab, agent);
    Assert.Null(cell.PlacementLine);
    Assert.Equal("by hand", cell.Title);
  }

  /// <summary>An agent record carrying the workspace it belongs to, which is what resolves its
  ///     tab. The parent id is deliberately NOT set to the tab's session: nothing may depend on
  ///     it, and a test that named one would hide a regression that started reading it again.
  ///     <para>The <c>AgentRecord.Spawned</c> factory is the DOMAIN's name for 'a new agent row'
  ///     and is not this path's to rename - retiring it is #334's work. What this issue owns is
  ///     that nothing in the DESKTOP reads its parent link to decide where a cell belongs.
  ///     </para></summary>
  private static AgentRecord Record(AgentId agent, string label, string workspaceRoot)
      => AgentRecord.Spawned(agent, parentId: null, depth: 1, "test/model", label, "p",
          DateTimeOffset.UtcNow, workspaceId: Shell.WorkspaceId(workspaceRoot));

  /// <summary>A chain whose step at <paramref name="position"/> is owned by
  ///     <paramref name="agent"/>, so the placement read has the link it reads.</summary>
  private static Chain ChainWith(AgentId agent, int position, int steps)
  {
    Chain chain = Chain.NewFromSeeds(Shell.WorkspaceId(@"C:\work\alpha"), planId: 1, planVersion: 1,
        "the chain", sessionId: null,
        [.. Enumerable.Range(1, steps).Select(p => ChainStepSeed.Implementing(p))],
        DateTimeOffset.UtcNow) with
    { Id = 7 };
    return chain with
    {
      Steps = [.. chain.Steps.Select(step => step.Position == position
          ? step with { SessionId = agent.ToString() }
          : step)],
    };
  }
}
