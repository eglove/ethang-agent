using System.Diagnostics;
using eThangAgent.AgentDomain;
using eThangAgent.AgentInfrastructure;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>Issue #329: a spawned agent becomes a grid cell. The event source is #326's - a
///     launched host's forwarded child events republish onto the container that launched it -
///     so what is asserted here is the step after that: the shell turns a
///     <see cref="ChildStartedEvent"/> into exactly one cell in the spawning agent's tab, and
///     a <see cref="ChildSettledEvent"/> into a finished card.
///     <para>WHY THIS DRIVES THE REAL SHELL RATHER THAN THE SUBSCRIBER DIRECTLY: the defect
///     being closed is a MISSING CALLER. <c>MainViewModel.AddCellAsync</c> and
///     <c>AgentTabViewModel.AddCell</c> had zero call sites, so a test that built a
///     subscriber by hand would pass with the subscription never attached - which is exactly
///     the state the parent issue found. Every test here goes through
///     <c>OpenAgentAsync</c> and the session's own stream, so removing the wiring in
///     <c>AttachSpawnedCells</c> fails them.</para>
///     <para>NO POLLING: cells come from the event. The transcript inside a cell is read from
///     the shared store (parent #325 D4), but the CELL ITSELF is created by the started fact -
///     a design where <c>ListChildrenAsync</c> created cells would resurrect every child the
///     workspace ever ran.</para></summary>
public class SpawnedAgentCellTests
{
  /// <summary>A session whose container carries a real event stream and store, so the shell's
  ///     own attach path finds both. The turn runner never runs: these tests spawn, they do
  ///     not converse.</summary>
  /// <param name="root">The workspace root the session addresses.</param>
  /// <param name="events">The container's stream, which the shell subscribes.</param>
  /// <param name="store">The store the spawned child's record and transcript land in.</param>
  internal static AgentSession FakeSession(string root, IAgentEvents events, IAgentStore store)
  {
    ServiceProvider services = new ServiceCollection()
        .AddSingleton(events)
        .AddSingleton(store)
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

  /// <summary>A store that answers the child's record and transcript, so the cell's label
  ///     and its transcript are drivable without a database.</summary>
  private sealed class SpawnStore : IAgentStore
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

  /// <summary>The composed shell plus the streams and store it was given.</summary>
  private sealed class Shell
  {
    public InProcessAgentEvents Events { get; } = new();

    public SpawnStore Store { get; } = new();

    public MainViewModel ViewModel { get; }

    public Shell()
    {
      ViewModel = new MainViewModel(
          (root, provider) => Task.FromResult(Result.Success(
              FakeSession(root, Events, Store))),
          new MainViewModelOptions
          {
            // Synchronous: a posted cell mutation would never run without a dispatcher loop.
            UiMarshal = action => action(),
          });
    }

    public async Task<AgentTabViewModel> OpenAsync(string root)
    {
      Result<AgentTabViewModel> opened = await ViewModel.OpenAgentAsync(root, "openrouter").ConfigureAwait(true);
      Assert.True(opened.IsSuccess, opened.Error?.Message);
      return opened.Value;
    }
  }

  /// <summary>Waits for the cell showing <paramref name="childId"/> to appear. The cell is
  ///     appended from an async store read, so the wait is on the observable outcome rather
  ///     than on a fixed delay.</summary>
  /// <param name="tab">The tab the cell is expected in.</param>
  /// <param name="childId">The spawned agent's id.</param>
  private static async Task<AgentCellViewModel> AwaitCellAsync(AgentTabViewModel tab, AgentId childId)
  {
    DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
    while (DateTimeOffset.UtcNow < deadline)
    {
      if (tab.FindCell(childId) is { } cell)
      {
        return cell;
      }

      await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    Assert.Fail($"no cell appeared for child {childId}; the tab holds {tab.Cells.Count} cell(s).");
    throw new UnreachableException();
  }

  /// <summary>The headline: a spawned agent becomes ONE cell in the spawning agent's tab,
  ///     captioned with the label its spawn named (D9).
  ///     <para>THE LABEL IS THE ASSERTION THAT THE RECORD WAS READ: the started event carries
  ///     no label, so a cell showing 'reviewer' proves the cell was built from the child's own
  ///     record rather than from the event's metadata alone.</para></summary>
  [Fact]
  public async Task AStartedEvent_AppendsOneCell_WithTheSpawnLabel()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    AgentId child = AgentId.NewId();
    shell.Store.Records[child.Value] = AgentRecord.Spawned(child, tab.Container.RootId, 1,
        "test/model", "reviewer", "review the diff", DateTimeOffset.UtcNow);

    shell.Events.Publish(new ChildStartedEvent(child, DateTimeOffset.UtcNow,
        tab.Container.RootId, "test/model", 1));

    AgentCellViewModel cell = await AwaitCellAsync(tab, child);
    Assert.True(cell.IsSpawned);
    Assert.Equal("reviewer", cell.Title);
    Assert.Equal(child, cell.RootId);
    Assert.Null(cell.Container);
  }

  /// <summary>Exactly one cell: a second started fact for the SAME agent is a watchdog retry
  ///     of one agent, not a second agent, so it must not append a duplicate.
  ///     <para>This is the duplicate the app-side republish makes possible to get wrong, and
  ///     the grid would show it as two agents doing one job.</para></summary>
  [Fact]
  public async Task ARepeatedStartedEvent_DoesNotAppendASecondCell()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    AgentId child = AgentId.NewId();
    shell.Store.Records[child.Value] = AgentRecord.Spawned(child, tab.Container.RootId, 1,
        "test/model", "reviewer", "review the diff", DateTimeOffset.UtcNow);
    ChildStartedEvent started = new(child, DateTimeOffset.UtcNow, tab.Container.RootId, "test/model", 1);

    shell.Events.Publish(started);
    _ = await AwaitCellAsync(tab, child);
    shell.Events.Publish(started with { Attempts = 2 });
    // The retry's append (if any) would be a second cell; give it a chance to land.
    await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);

    Assert.Equal(2, tab.Cells.Count);
    _ = Assert.Single(tab.Cells, c => c.RootId == child);
  }

  /// <summary>D13/D2: the primary session stays cell 0 and the spawned agent appends after
  ///     it - the tab's identity never moves, and a spawn never takes the first slot.
  ///     <para>D2's 'no steal' half is asserted too: nothing here focuses or selects, because
  ///     there is no code path that could. The cell is appended and the tab's primary
  ///     view-model is untouched.</para></summary>
  [Fact]
  public async Task ASpawnedCell_AppendsAfterThePrimary_AndThePrimaryStaysCellZero()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    AgentCellViewModel primary = tab.Cells[0];
    AgentId first = AgentId.NewId();
    AgentId second = AgentId.NewId();
    shell.Store.Records[first.Value] = AgentRecord.Spawned(first, tab.Container.RootId, 1,
        "test/model", "one", "p", DateTimeOffset.UtcNow);
    shell.Store.Records[second.Value] = AgentRecord.Spawned(second, tab.Container.RootId, 1,
        "test/model", "two", "p", DateTimeOffset.UtcNow);

    shell.Events.Publish(new ChildStartedEvent(first, DateTimeOffset.UtcNow, tab.Container.RootId, "test/model", 1));
    _ = await AwaitCellAsync(tab, first);
    shell.Events.Publish(new ChildStartedEvent(second, DateTimeOffset.UtcNow, tab.Container.RootId, "test/model", 1));
    _ = await AwaitCellAsync(tab, second);

    Assert.Equal(3, tab.Cells.Count);
    Assert.Same(primary, tab.Cells[0]);
    Assert.Equal(first, tab.Cells[1].RootId);
    Assert.Equal(second, tab.Cells[2].RootId);
    Assert.Same(primary.ViewModel, tab.ViewModel);
  }

  /// <summary>A settled fact finishes the cell: the card dims, its ✕ appears (D6), and the
  ///     agent stops counting as streaming. The card is NOT removed (D7).</summary>
  [Fact]
  public async Task ASettledEvent_FinishesTheCell_WithoutRemovingIt()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    AgentId child = AgentId.NewId();
    shell.Store.Records[child.Value] = AgentRecord.Spawned(child, tab.Container.RootId, 1,
        "test/model", "reviewer", "review the diff", DateTimeOffset.UtcNow);
    shell.Events.Publish(new ChildStartedEvent(child, DateTimeOffset.UtcNow, tab.Container.RootId, "test/model", 1));
    AgentCellViewModel cell = await AwaitCellAsync(tab, child);
    Assert.False(cell.IsFinished);
    Assert.True(cell.IsStreaming, "a running spawned agent streams until it settles");

    shell.Events.Publish(new ChildSettledEvent(child, DateTimeOffset.UtcNow,
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

  /// <summary>The child's transcript renders in its cell (parent #325 D4): the messages the
  ///     shared store holds are appended, so the card shows what the agent actually did.
  ///     <para>THE MECHANISM IS THE STORE, not the event stream: the child persists in its own
  ///     process, so the store is the only seam that carries message CONTENT. D4 chose a
  ///     500 ms poll for it and one final read after settle.</para></summary>
  [Fact]
  public async Task ASpawnedCellsTranscript_ComesFromTheSharedStore()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    AgentId child = AgentId.NewId();
    shell.Store.Records[child.Value] = AgentRecord.Spawned(child, tab.Container.RootId, 1,
        "test/model", "reviewer", "review the diff", DateTimeOffset.UtcNow);
    DateTimeOffset at = DateTimeOffset.UtcNow;
    shell.Store.Transcripts[child.Value] =
        [new Message(Role.User, "review the diff", at), new Message(Role.Assistant, "the diff is correct", at)];

    shell.Events.Publish(new ChildStartedEvent(child, DateTimeOffset.UtcNow, tab.Container.RootId, "test/model", 1));
    AgentCellViewModel cell = await AwaitCellAsync(tab, child);

    DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
    while (!cell.ViewModel.Transcript.AssistantText.Contains("the diff is correct", StringComparison.Ordinal)
        && DateTimeOffset.UtcNow < deadline)
    {
      await Task.Delay(TimeSpan.FromMilliseconds(25), TestContext.Current.CancellationToken);
    }

    Assert.Contains("the diff is correct", cell.ViewModel.Transcript.AssistantText, StringComparison.Ordinal);
  }

  /// <summary>A spawn from a session that is not open in this shell appends NO cell: a cell
  ///     can only exist inside an open tab, and inventing one would open a workspace the user
  ///     did not ask for.
  ///     <para>This is the negative half of the wiring: it proves the tab is resolved from the
  ///     PARENT rather than guessed, so a spawn cannot land in an unrelated workspace's grid.
  ///     </para></summary>
  [Fact]
  public async Task ASpawnFromAnUnopenedSession_AppendsNoCell()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    AgentId child = AgentId.NewId();
    shell.Store.Records[child.Value] = AgentRecord.Spawned(child, AgentId.NewId(), 1,
        "test/model", "orphan", "p", DateTimeOffset.UtcNow);

    shell.Events.Publish(new ChildStartedEvent(child, DateTimeOffset.UtcNow,
        AgentId.NewId(), "test/model", 1));
    await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);

    _ = Assert.Single(tab.Cells);
    Assert.DoesNotContain(tab.Cells, c => c.RootId == child);
  }
}
