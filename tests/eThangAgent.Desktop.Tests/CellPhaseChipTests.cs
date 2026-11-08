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

/// <summary>Issue #331: a cell's header shows its label, a LIVE phase chip, and - once the agent
///     settles - its final report.
///     <para>EVERY TEST DRIVES THE REAL SHELL. The chip's vocabulary is trivial to get right in
///     isolation; what the issue is about is that a progress fact REACHES a cell at all, which
///     is the step <c>AgentCellSubscriber</c> used to drop. A test that called the cell's own
///     apply method would pass with the routing still missing, so nothing here does.</para>
///     <para>THE REPORT IS READ FROM THE STORE, not from the settled fact:
///     <see cref="AgentSettledEvent"/> carries a byte count as a size hint, so a card that
///     showed a report proves the read happened.</para></summary>
public class CellPhaseChipTests
{
  /// <summary>A cell that has heard no beat reads <c>Starting</c>: the cell exists, and the
  ///     agent has said nothing about what it is doing yet.</summary>
  [Fact]
  public async Task AFreshCell_ReadsStarting()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");

    AgentCellViewModel cell = await shell.StartAgentAsync(tab, "reviewer");

    Assert.Equal(CellPhaseChip.Starting, cell.PhaseChip);
  }

  /// <summary>The chip moves through the fixed vocabulary as beats arrive, IN ORDER and with
  ///     none dropped: Starting, then Thinking for the model-call labels, then Running NAME for
  ///     a tool dispatch.
  ///     <para>THE OBSERVED SEQUENCE IS THE ASSERTION, not the final value. A chip that jumped
  ///     straight to its last state would still read correctly at the end while having dropped
  ///     every beat in between, which is exactly the defect the issue names.</para></summary>
  [Fact]
  public async Task ProgressBeats_MoveTheChipThroughTheVocabulary_InOrder()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    AgentCellViewModel cell = await shell.StartAgentAsync(tab, "reviewer");
    List<string> observed = [];
    cell.PropertyChanged += (_, e) =>
    {
      if (e.PropertyName == nameof(AgentCellViewModel.PhaseChip))
      {
        observed.Add(cell.PhaseChip);
      }
    };

    shell.Progress(cell.RootId, "turn-start");
    shell.Progress(cell.RootId, "iteration");
    shell.Progress(cell.RootId, "tool:exec");

    Assert.Equal(
        [CellPhaseChip.Thinking, CellPhaseChip.Thinking, CellPhaseChip.Running("exec")],
        observed);
    Assert.Equal("Running exec", cell.PhaseChip);
  }

  /// <summary>An unrecognised label falls back to <c>Thinking</c> rather than rendering the wire
  ///     vocabulary: a chip that sometimes shows <c>turn-start</c> is worse than one that is
  ///     occasionally vague.</summary>
  [Fact]
  public async Task AnUnrecognisedLabel_FallsBackToThinking()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    AgentCellViewModel cell = await shell.StartAgentAsync(tab, "reviewer");

    shell.Progress(cell.RootId, "overflow-recovery");

    Assert.Equal(CellPhaseChip.Thinking, cell.PhaseChip);
  }

  /// <summary>A beat that repeats the current chip still notifies: a long tool run would
  ///     otherwise leave the header looking frozen for its whole duration.</summary>
  [Fact]
  public async Task ARepeatedBeat_StillNotifies()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    AgentCellViewModel cell = await shell.StartAgentAsync(tab, "reviewer");
    shell.Progress(cell.RootId, "iteration");
    int raises = 0;
    cell.PropertyChanged += (_, e) =>
    {
      if (e.PropertyName == nameof(AgentCellViewModel.PhaseChip))
      {
        raises++;
      }
    };

    shell.Progress(cell.RootId, "iteration");

    Assert.Equal(1, raises);
  }

  /// <summary>A beat naming an agent no tab shows is dropped, exactly as a settle is: a cell
  ///     can only exist inside an open tab, and inventing one would open a workspace nobody
  ///     asked for.</summary>
  [Fact]
  public async Task ABeatForAnUnknownAgent_ChangesNothing()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    AgentCellViewModel cell = await shell.StartAgentAsync(tab, "reviewer");

    shell.Progress(AgentId.NewId(), "tool:exec");

    Assert.Equal(CellPhaseChip.Starting, cell.PhaseChip);
  }

  /// <summary>A beat that arrives BEFORE its agent's cell exists is held and applied when the
  ///     cell lands, in order (issue #331 review F2).
  ///     <para>THE STORE ANSWERS SLOWLY ON PURPOSE. Cell creation awaits a record read and a
  ///     chain read; a beat costs one marshal. The spawner publishes the started fact and the
  ///     loop's first beats back to back, so with a synchronous store the cell always exists
  ///     first and the race this test exists for never happens - which is why the earlier
  ///     ordering test could not catch the drop.</para>
  ///     <para>THE FINAL CHIP IS THE ASSERTION, and it names the LAST beat: a subscriber that
  ///     dropped the beats leaves <c>Starting</c>, and one that applied only the first leaves
  ///     <c>Thinking</c>.</para></summary>
  [Fact]
  public async Task ABeatBeforeItsCellExists_IsHeldAndAppliedWhenTheCellLands()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    AgentId agent = shell.RegisterAgent(tab, "reviewer");
    shell.Store.RecordReadDelay = TimeSpan.FromMilliseconds(60);

    shell.Events.Publish(new AgentStartedEvent(agent, DateTimeOffset.UtcNow, "test/model", 1));
    shell.Progress(agent, "turn-start");
    shell.Progress(agent, "tool:exec");

    AgentCellViewModel cell = await Shell.WaitForCellAsync(tab, agent);

    Assert.Equal(CellPhaseChip.Running("exec"), cell.PhaseChip);
  }

  /// <summary>A SETTLE that arrives before its cell exists is held the same way, so a short-lived
  ///     agent does not leave a card that runs forever (issue #331 review F2).
  ///     <para>This is the same gap seen from the other end: the settle is a fact like any other,
  ///     and it arrives while the cell is still being created whenever the agent finishes faster
  ///     than a store read.</para></summary>
  [Fact]
  public async Task ASettleBeforeItsCellExists_IsHeldAndFinishesTheCard()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    AgentId agent = shell.RegisterAgent(tab, "reviewer");
    shell.Store.SetReport(agent, "too short a run to see.");
    shell.Store.RecordReadDelay = TimeSpan.FromMilliseconds(60);

    shell.Events.Publish(new AgentStartedEvent(agent, DateTimeOffset.UtcNow, "test/model", 1));
    shell.Settle(agent);

    AgentCellViewModel cell = await Shell.WaitForCellAsync(tab, agent);

    Assert.True(cell.IsFinished, "a settle that preceded the cell must still finish it");
    await WaitUntilAsync(() => cell.PhaseChip == CellPhaseChip.Finished);
    Assert.Equal("too short a run to see.", cell.Report);
  }

  /// <summary>A settled cell reads <c>Finished</c> and renders the agent's report, which is
  ///     read from the shared store (parent D5).</summary>
  [Fact]
  public async Task ASettledCell_ReadsFinished_AndRendersTheReport()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    AgentCellViewModel cell = await shell.StartAgentAsync(tab, "reviewer");
    shell.Store.SetReport(cell.RootId, "the diff is correct; nothing to change.");

    shell.Settle(cell.RootId);

    await WaitUntilAsync(() => cell.PhaseChip == CellPhaseChip.Finished);
    Assert.True(cell.HasReport);
    Assert.Equal("the diff is correct; nothing to change.", cell.Report);
    Assert.Null(cell.ReportNotice);
  }

  /// <summary>A report that cannot be read leaves the chip at <c>Finished</c> and STATES the
  ///     failure: an empty panel would read as 'this agent concluded nothing', which is a
  ///     different fact from 'the report could not be read'.</summary>
  [Fact]
  public async Task AReportReadFailure_StaysFinished_AndStatesWhy()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    AgentCellViewModel cell = await shell.StartAgentAsync(tab, "reviewer");
    shell.Store.FailRecordReads = true;

    shell.Settle(cell.RootId);

    await WaitUntilAsync(() => cell.PhaseChip == CellPhaseChip.Finished);
    Assert.False(cell.HasReport);
    Assert.Equal(AgentCellViewModel.ReportUnavailableNotice, cell.ReportNotice);
  }

  /// <summary>An idle alert sets the cell's outline and appends exactly ONE line to its own
  ///     transcript (parent D12, corrected for the parentless model: there is no parent's
  ///     transcript to write into).</summary>
  [Fact]
  public async Task AnIdleAlert_SetsTheOutline_AndAppendsOneLine()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    AgentCellViewModel cell = await shell.StartAgentAsync(tab, "reviewer");
    int before = cell.ViewModel.Transcript.Notices.Count;

    shell.Events.Publish(new AgentIdleAlertEvent(cell.RootId, DateTimeOffset.UtcNow,
        TimeSpan.FromMinutes(12), "ModelCall"));

    Assert.True(cell.IsAlerting);
    Assert.Equal(before + 1, cell.ViewModel.Transcript.Notices.Count);
    Assert.Contains("idle", cell.ViewModel.Transcript.Notices[^1], StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>A budget alert does the same, and a REPEATED alert does not stack a second line:
  ///     an alert re-raises while a run stays over its threshold, so the line must be written
  ///     once per alert rather than once per tick.</summary>
  [Fact]
  public async Task ABudgetAlert_SetsTheOutline_AndRepeatsDoNotStackLines()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    AgentCellViewModel cell = await shell.StartAgentAsync(tab, "reviewer");
    int before = cell.ViewModel.Transcript.Notices.Count;
    AgentBudgetAlertEvent alert = new(cell.RootId, DateTimeOffset.UtcNow, "tokens", 800_000, 1_000_000, 12_000);

    shell.Events.Publish(alert);
    shell.Events.Publish(alert);

    Assert.True(cell.IsAlerting);
    Assert.Equal(before + 1, cell.ViewModel.Transcript.Notices.Count);
  }

  /// <summary>An alert for an agent no tab shows sets no outline and appends nothing: the same
  ///     rule a beat and a settle follow.</summary>
  [Fact]
  public async Task AnAlertForAnUnknownAgent_ChangesNothing()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    AgentCellViewModel cell = await shell.StartAgentAsync(tab, "reviewer");
    int before = cell.ViewModel.Transcript.Notices.Count;

    shell.Events.Publish(new AgentIdleAlertEvent(AgentId.NewId(), DateTimeOffset.UtcNow,
        TimeSpan.FromMinutes(9), "ModelCall"));

    Assert.False(cell.IsAlerting);
    Assert.Equal(before, cell.ViewModel.Transcript.Notices.Count);
  }

  /// <summary>A RESUMED agent's card forgets the previous run's phase and report (issue #330's
  ///     resume path): leaving them would describe a run that is over.</summary>
  [Fact]
  public async Task AResumedAgent_ResetsTheChipAndTheReport()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    AgentCellViewModel cell = await shell.StartAgentAsync(tab, "reviewer");
    shell.Store.SetReport(cell.RootId, "first run concluded X.");
    shell.Settle(cell.RootId);
    await WaitUntilAsync(() => cell.PhaseChip == CellPhaseChip.Finished);
    Assert.True(cell.HasReport);

    // The started fact for an agent this tab already shows is a RESTART of the same agent.
    shell.Events.Publish(new AgentStartedEvent(cell.RootId, DateTimeOffset.UtcNow, "test/model", 2));

    await WaitUntilAsync(() => !cell.IsFinished);
    Assert.Equal(CellPhaseChip.Starting, cell.PhaseChip);
    Assert.False(cell.HasReport);
  }

  /// <summary>A RETRIED START WHOSE SETTLE LANDS FIRST STILL FINISHES THE CARD (issue #331 review
  ///     F3). A resume republishes the started fact for an agent that ALREADY has a cell, and the
  ///     resumed run can settle while that fact's record read is still outstanding.
  ///     <para>THE STORE READ IS ASYNCHRONOUS ON PURPOSE: the retried start's decision about the
  ///     existing cell lands after the settle has already been routed, which is the interleaving
  ///     that left the card reading <c>Starting</c> forever with no report.</para>
  ///     <para>THE NEW REPORT IS THE ASSERTION, not merely <c>Finished</c>: the resume reset
  ///     clears the previous run's conclusion, so a card that kept it would be showing a report
  ///     for a run that is over.</para></summary>
  [Fact]
  public async Task ARetriedStartWhoseSettleLandsFirst_StillFinishesTheCard()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha");
    AgentCellViewModel cell = await shell.StartAgentAsync(tab, "reviewer");
    shell.Store.SetReport(cell.RootId, "the first run concluded X.");
    shell.Settle(cell.RootId);
    await WaitUntilAsync(() => cell.PhaseChip == CellPhaseChip.Finished);
    Assert.Equal("the first run concluded X.", cell.Report);

    // The resume: the retried started fact and the settle the resumed run produces arrive back
    // to back, while the started fact's own record read is still outstanding.
    shell.Store.SetReport(cell.RootId, "the resumed run concluded Y.");
    shell.Store.RecordReadDelay = TimeSpan.FromMilliseconds(60);
    shell.Events.Publish(new AgentStartedEvent(cell.RootId, DateTimeOffset.UtcNow, "test/model", 2));
    shell.Settle(cell.RootId);

    // ONE CONDITION FOR THE WHOLE END STATE: the card is finished, its chip has stopped settling,
    // and it carries the RESUMED run's report. Asserting 'finished' alone would pass on the
    // previous run's card before the resume reset landed, which is not the outcome being pinned.
    await WaitUntilAsync(() => cell.IsFinished
        && cell.PhaseChip == CellPhaseChip.Finished
        && cell.Report == "the resumed run concluded Y.");
    Assert.Equal("the resumed run concluded Y.", cell.Report);
    Assert.Null(cell.ReportNotice);
  }

  /// <summary>Waits for an observable outcome rather than a fixed delay, so a slow machine
  ///     cannot turn a correct implementation into a red test.</summary>
  /// <param name="condition">The outcome to wait for.</param>
  private static async Task WaitUntilAsync(Func<bool> condition)
  {
    DateTimeOffset deadline = DateTimeOffset.UtcNow + TestCeiling.Value;
    while (!condition() && DateTimeOffset.UtcNow < deadline)
    {
      await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken)
          .ConfigureAwait(true);
    }

    Assert.True(condition(), "the expected outcome never arrived.");
  }

  /// <summary>The composed shell plus the stream and the store it resolves, so every test goes
  ///     through the real attach path in <c>MainViewModel.AttachAgentCells</c>.
  ///     <para>THE MARSHAL IS SYNCHRONOUS: a posted cell mutation would never run without a
  ///     dispatcher loop, and the beats then apply inline in publication order - which is what
  ///     makes the ordering assertion meaningful rather than a race.</para></summary>
  private sealed class Shell
  {
    public InProcessAgentEvents Events { get; } = new();

    public Store Store { get; } = new();

    public MainViewModel ViewModel { get; }

    public Shell()
    {
      ViewModel = new MainViewModel(
          (root, provider) => Task.FromResult(Result.Success(Session(root))),
          new MainViewModelOptions { UiMarshal = action => action() });
    }

    public static string WorkspaceId(string root) => "ws:" + root;

    public async Task<AgentTabViewModel> OpenAsync(string root)
    {
      Result<AgentTabViewModel> opened = await ViewModel.OpenAgentAsync(root, "openrouter").ConfigureAwait(true);
      Assert.True(opened.IsSuccess, opened.Error?.Message);
      return opened.Value;
    }

    /// <summary>Starts one agent in the tab's workspace and waits for its cell: the started
    ///     fact is the only thing that creates a cell, so a test cannot skip it.</summary>
    /// <param name="tab">The tab the agent belongs to.</param>
    /// <param name="label">The label its record carries.</param>
    public async Task<AgentCellViewModel> StartAgentAsync(AgentTabViewModel tab, string label)
    {
      AgentId agent = RegisterAgent(tab, label);
      Events.Publish(new AgentStartedEvent(agent, DateTimeOffset.UtcNow, "test/model", 1));
      return await WaitForCellAsync(tab, agent).ConfigureAwait(true);
    }

    /// <summary>Writes an agent's record into the store and returns its id, WITHOUT publishing
    ///     the started fact: a test that needs the record to exist before the fact is what
    ///     creates the gap between 'the agent started' and 'its cell exists'.
    ///     <para>The record carries the tab's own workspace, which is what resolves the tab the
    ///     cell belongs to (D10) - the same path production takes.</para></summary>
    /// <param name="tab">The tab whose workspace the agent belongs to.</param>
    /// <param name="label">The label its record carries.</param>
    public AgentId RegisterAgent(AgentTabViewModel tab, string label)
    {
      ArgumentNullException.ThrowIfNull(tab);
      AgentId agent = AgentId.NewId();
      Store.Records[agent.Value] = AgentRecord.Started(agent, "test/model", label, "p", DateTimeOffset.UtcNow,
          workspaceId: WorkspaceId(tab.Container.WorkspaceRoot));
      return agent;
    }

    /// <summary>Waits for the started fact's cell to appear, so a test that published facts
    ///     before it existed can still read the result.</summary>
    /// <param name="tab">The tab the cell belongs to.</param>
    /// <param name="agent">The agent whose cell to wait for.</param>
    public static async Task<AgentCellViewModel> WaitForCellAsync(AgentTabViewModel tab, AgentId agent)
    {
      DateTimeOffset deadline = DateTimeOffset.UtcNow + TestCeiling.Value;
      while (tab.FindCell(agent) is null && DateTimeOffset.UtcNow < deadline)
      {
        await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
      }

      return tab.FindCell(agent) ?? throw new InvalidOperationException("no cell appeared.");
    }

    /// <summary>Publishes one progress beat for an agent.</summary>
    /// <param name="agentId">The agent the beat names.</param>
    /// <param name="label">The beat's short phase label.</param>
    public void Progress(AgentId agentId, string label)
        => Events.Publish(new AgentProgressEvent(agentId, DateTimeOffset.UtcNow,
            AgentPhase.ModelCall, label));

    /// <summary>Publishes the settled fact the shell turns into a finished card.</summary>
    /// <param name="agentId">The agent that settled.</param>
    public void Settle(AgentId agentId)
        => Events.Publish(new AgentSettledEvent(agentId, DateTimeOffset.UtcNow,
            AgentStatus.Completed, null, 12));

    private AgentSession Session(string root)
    {
      ServiceProvider services = new ServiceCollection()
          .AddSingleton<IAgentEvents>(Events)
          .AddSingleton<IAgentStore>(Store)
          .AddSingleton<IWorkspaceContext>(new FixedWorkspaceContext(WorkspaceId(root), root))
          .BuildServiceProvider();
      return new AgentSession(
          services,
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

  /// <summary>The shared store the cells read: records for the label, workspace, and report,
  ///     transcripts for the card's content.
  ///     <para><see cref="FailRecordReads"/> drives the 'the report could not be read' contract
  ///     without breaking the started fact's own record read, which has already happened by the
  ///     time a test sets it.</para></summary>
  private sealed class Store : IAgentStore
  {
    public Dictionary<Guid, AgentRecord> Records { get; } = [];

    /// <summary>When true every record read fails, so the report read cannot succeed.</summary>
    public bool FailRecordReads { get; set; }

    /// <summary>Stores the agent's final report the way a settling run does: on its record.</summary>
    /// <param name="agentId">The agent whose report it is.</param>
    /// <param name="report">The report text.</param>
    public void SetReport(AgentId agentId, string report)
        => Records[agentId.Value] = Records[agentId.Value] with
        {
          Status = AgentStatus.Completed,
          FinalReport = report,
        };

    public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success("saved"));

    public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success("updated"));

    /// <summary>How long a record read takes, so a test can make cell creation genuinely
    ///     asynchronous. Zero is the ordinary instant answer.
    ///     <para>A DELAY, NOT A GATE: the started fact's read must still complete on its own, or
    ///     the cell would never appear and the test would be asserting a deadlock.</para></summary>
    public TimeSpan RecordReadDelay { get; set; }

    public async Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
    {
      if (RecordReadDelay > TimeSpan.Zero)
      {
        await Task.Delay(RecordReadDelay, ct).ConfigureAwait(false);
      }

      return FailRecordReads ? Unreadable() : Find(id);
    }

    private static Result<AgentRecord> Unreadable()
        => Result.Failure<AgentRecord>(new DomainError("StoreFault", "unreadable"));

    private Result<AgentRecord> Find(AgentId id)
        => Records.TryGetValue(id.Value, out AgentRecord? record)
            ? Result.Success(record)
            : Result.Failure<AgentRecord>(new DomainError("NotFound", "not found"));

    public Task<Result<string>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default)
        => Task.FromResult(Result.Success("appended"));

    public Task<Result<string>> ReplaceTranscriptAsync(AgentId id, IReadOnlyList<Message> messages,
        CancellationToken ct = default) => Task.FromResult(Result.Success(id.ToString()));

    public Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<Message>>([]));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([.. Records.Values]));
  }
}
