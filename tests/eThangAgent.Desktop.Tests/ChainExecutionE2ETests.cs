using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;
using eThangAgent.Storage.ACL;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>A chain assembled END TO END: a model-authored <c>chain.start</c> converts a plan and
///     launches step 1's session over the REAL <c>session-request</c> wire, with the record
///     asserted at each boundary. The second test pins where the assembly BREAKS today - the
///     step 1 to step 2 advance - against issue #322.
///
///     <para>THE FORK, STATED. This takes OPTION (b): a REAL <c>ChildHost</c> per session,
///     launched by the production <see cref="AgentSessionFactory"/>, with the chain launched
///     over the production wire envelope. The reason is structural rather than a taste for
///     fidelity: option (a) CANNOT RUN THIS CHAIN AT ALL. An in-process container
///     (<c>AddInProcessAgentRuntime</c>) deliberately REMOVES <c>IRootTurnChannel</c> and no
///     <c>RootRunHost</c> exists, so <c>NewSessionRequestChannel.Connect</c> is never called and
///     <c>WireChainStartPort</c> fails every launch immediately with
///     <c>SessionRequestUnreachable</c>. An in-process chain test would therefore have to FAKE
///     <c>IChainStartPort</c> - the one port that IS the chain's launch mechanism - which is
///     exactly the green-test-over-a-broken-real-path failure this codebase keeps recording.
///     So the whole path runs for real: model-authored exec -> <c>Tools.Invoke("chain.start")</c>
///     -> <c>ChainCapabilityProvider</c> -> <c>WireChainStartPort</c> ->
///     <c>NewSessionRequestChannel</c> -> <c>session-request</c> envelope ->
///     <c>RemoteAgentRuntime</c> pump -> <c>AgentSessionViewModel</c> ->
///     <c>MainViewModel.HandleSessionRequestAsync</c> -> a brief-seeded session built by the
///     factory -> the answer returns as <c>session-response</c> -> the chain reassigns the claim.
///     </para>
///
///     <para>WHAT IT LEAVES UNTESTED: it does not prove a REAL MODEL would do this. The provider
///     is the mock, so each step's session obeys a scripted exec program rather than reading its
///     brief and deciding. A live run is the only thing that settles whether a brief is
///     sufficient instruction, and that is stated again at the end of the class.</para>
///
///     <para>THE GATE IS ARRANGED, NOT TRIPPED (issue #304). <c>goal/check</c> is declared in the
///     test workspace as <c>exit 0</c> - portable across the PowerShell and cmd fallbacks, and
///     always green - and each step's program runs <c>state.checkgoal</c> IMMEDIATELY before its
///     <c>Done</c>, which is exactly what the <c>executing-a-chain</c> skill tells a real session
///     to do. So the gate is genuinely fresh at the step boundary; a chain that could only
///     progress by bypassing it would fail here rather than quietly pass.</para>
///
///     <para>THE HUMAN'S ONE CHANNEL IS USED DELIBERATELY. A chain step's session is launched
///     SEED-ONLY (no first turn) and nothing in this build fires one, so this test types into
///     the step's cell the way a person would - the surviving steering channel (#316 D1). It
///     does not invent a production mechanism that does not exist.</para></summary>
[Collection("Desktop E2E")]
public class ChainExecutionE2ETests
{
  /// <summary>How long one step's turn may take. Generous because <c>chain.start</c> BLOCKS
  ///     while the app builds a container and launches a host process for the step's session.
  ///     </summary>
  private static readonly TimeSpan StepLimit = TimeSpan.FromSeconds(180);

  private static readonly TimeSpan SwapLimit = TimeSpan.FromSeconds(120);

  /// <summary>The creating session's turn: plan, then the explicit plan-to-execution
  ///     transition. <c>chain.start</c> blocks until the app answers with step 1's session id.
  ///     The database is fresh, so the plan id is deterministically 1.</summary>
  private const string CreatorProgram = """
      var created = Tools.Invoke("plan.create", new { timeoutSeconds = 60,
          title = "Chain E2E", goal = "Prove a chain executes end to end.",
          steps = new[] { new { title = "first step" }, new { title = "second step" } } });
      var started = Tools.Invoke("chain.start", new { timeoutSeconds = 300, planId = 1 });
      return created + "|" + started;
      """;

  /// <summary>BOUNDARIES 1 AND 2, which the assembly passes: the model's <c>chain.start</c>
  ///     crosses the wire, the app launches step 1's session through the real factory, and the
  ///     durable record names that session with step 2 still Pending.</summary>
  [AvaloniaFact]
  public async Task ChainStart_LaunchesStepOneSession_OverTheWire_AndRecordsIt()
  {
    using E2E.HostHarness host = new();
    _ = await host.StartAsync().ConfigureAwait(true);
    string workspace = Directory.CreateTempSubdirectory("ethang-chain-e2e").FullName;
    MainViewModel? shell = null;
    try
    {
      shell = await OpenShellAsync(host).ConfigureAwait(true);
      _ = host.Mock.ReturnsForModel(E2E.SessionModel,
          E2E.ExecToolCall("creator_1", ExecProgram(CreatorProgram)),
          E2E.RawCompletion("chain started"));

      Result<AgentTabViewModel> opened = await shell.OpenAgentAsync(workspace, Providers.OpenRouter)
          .ConfigureAwait(true);
      Assert.True(opened.IsSuccess, opened.Error?.Message);
      AgentSession creator = opened.Value.Container;
      AgentId creatorId = creator.RootId;
      string workspaceId = creator.Services.GetRequiredService<IWorkspaceContext>().WorkspaceId;
      await DeclareGateAsync(creator).ConfigureAwait(true);

      // ---- BOUNDARY 1: chain.start, over the wire, from a model-authored exec program ----
      await RunTurnAsync(shell.Tabs[0].ViewModel, "plan the work and start executing it", StepLimit)
          .ConfigureAwait(true);
      string startResult = E2E.GetLastToolMessage(host.Mock.RequestBodies[1]);
      Assert.Contains("[plan] created #1", startResult, StringComparison.Ordinal);
      Assert.Contains("[chain] created #1 from plan #1", startResult, StringComparison.Ordinal);

      // ---- BOUNDARY 2: step 1's session replaced the creator's cell, brief-seeded ----
      AgentId step1Id = await WaitForCellSwapAsync(shell, creatorId, SwapLimit).ConfigureAwait(true);
      Assert.NotEqual(creatorId, step1Id);
      // THE RECORD NAMES THE REAL SESSION, not the placeholder the claim was taken under:
      // creation claims step 1 as 'pending-<guid>' and REASSIGNS it after the launch answers, so
      // a single read here would race a documented two-step and assert a provisional id.
      Chain chain = await WaitForRecordAsync(host.DatabasePath, workspaceId,
          c => c.StepAt(1)?.SessionId == step1Id.ToString(), SwapLimit).ConfigureAwait(true);
      Assert.Equal(step1Id.ToString(), chain.StepAt(1)!.SessionId, StringComparer.Ordinal);
      Assert.Equal(ChainStatus.Running, chain.Status);
      Assert.Equal(ChainStepStatus.Running, chain.StepAt(1)!.Status);
      Assert.Equal(ChainStepStatus.Pending, chain.StepAt(2)!.Status);
      Assert.Null(chain.StepAt(2)!.SessionId);
      // The brief reached the session the model will read: it is the seeded first message.
      Assert.Contains(shell.Tabs[0].ViewModel.Transcript.Entries.OfType<SystemMessageEntry>(),
          entry => entry.Text.StartsWith(ChainBriefRenderer.Prefix, StringComparison.Ordinal));
    }
    finally
    {
      await ShutdownAsync(shell, workspace).ConfigureAwait(true);
    }
  }

  /// <summary>BOUNDARIES 3 AND 4, WHICH THE ASSEMBLY FAILS TODAY (issue #322). This test pins the
  ///     DEFECT rather than the intent, and it is written so the fix makes it fail: when a chain
  ///     advances, the <c>TurnBusy</c> refusal disappears and the final assertion goes red.
  ///
  ///     <para>THE DEFECT, MEASURED. The completion fact arrives MID-TURN - it is published by
  ///     the <c>plan.update-step ... status=Done</c> write, which happens inside the step's own
  ///     exec call, before that call even returns. <c>ChainAdvanceSubscriber</c> dispatches the
  ///     advance immediately, and the advance calls <c>MainViewModel.StartChainStepSessionAsync</c>
  ///     -> <c>StartNewSessionAsync</c>, which refuses a busy tab with <c>TurnBusy</c>. The tab IS
  ///     busy: it is running the very turn that reported the completion. So the launch is
  ///     refused, <c>StartStepAsync</c> releases the claim, and NOTHING RETRIES - the chain sits
  ///     <c>Running</c> with step 2 <c>Pending</c> and no session, forever, while every surface
  ///     reports it in progress. Observed verbatim in the transcript:
  ///     <c>[chain] advance refused [TurnBusy]: a turn is running in that tab; stop it before</c>
  ///     <c>starting a new session.</c></para>
  ///
  ///     <para>THE ASYMMETRY IS THE CAUSE, and it is visible in one comparison. The
  ///     <c>chain.start</c> launch goes through <c>HandleSessionRequestAsync</c>, which knows the
  ///     launch happens mid-turn and therefore ANSWERS FIRST and waits
  ///     (<c>WaitForIdleAndReplaceAsync</c>) before swapping. The ADVANCE path has no such wait: it
  ///     calls <c>StartNewSessionAsync</c> directly and is refused. No unit test can see this,
  ///     because <c>ChainAdvanceServiceTests</c> drives a fake <c>IChainAdvanceHost</c> that never
  ///     checks <c>IsBusy</c> and <c>StartChainStepSessionAsync</c> has no test of its own - the app
  ///     half of the advance was never assembled with the domain half.</para>
  ///
  ///     <para>WHY PIN THE BROKEN BEHAVIOUR: a skipped test would hide the bug and rot, and
  ///     deleting it would leave the path unassembled. Pinning it means the suite fails the
  ///     moment someone fixes the advance, which is when the assertion below must be replaced by
  ///     the real end state - step 2 running, then the chain <c>Completed</c>.</para></summary>
  [AvaloniaFact]
  public async Task ChainAdvance_AfterAStepCompletes_IsRefusedTurnBusy_AndTheChainStalls()
  {
    using E2E.HostHarness host = new();
    _ = await host.StartAsync().ConfigureAwait(true);
    string workspace = Directory.CreateTempSubdirectory("ethang-chain-e2e").FullName;
    MainViewModel? shell = null;
    try
    {
      shell = await OpenShellAsync(host).ConfigureAwait(true);
      _ = host.Mock.ReturnsForModel(E2E.SessionModel,
          E2E.ExecToolCall("creator_1", ExecProgram(CreatorProgram)),
          E2E.RawCompletion("chain started"),
          E2E.ExecToolCall("step1_1", ExecProgram(StepProgram(1))),
          E2E.RawCompletion("step 1 done"));

      Result<AgentTabViewModel> opened = await shell.OpenAgentAsync(workspace, Providers.OpenRouter)
          .ConfigureAwait(true);
      Assert.True(opened.IsSuccess, opened.Error?.Message);
      AgentSession creator = opened.Value.Container;
      AgentId creatorId = creator.RootId;
      string workspaceId = creator.Services.GetRequiredService<IWorkspaceContext>().WorkspaceId;
      await DeclareGateAsync(creator).ConfigureAwait(true);

      await RunTurnAsync(shell.Tabs[0].ViewModel, "plan the work and start executing it", StepLimit)
          .ConfigureAwait(true);
      AgentId step1Id = await WaitForCellSwapAsync(shell, creatorId, SwapLimit).ConfigureAwait(true);

      // ---- BOUNDARY 3: step 1 completes - and the advance is REFUSED, mid-turn ----
      await RunTurnAsync(shell.Tabs[0].ViewModel, "work your assigned step", StepLimit).ConfigureAwait(true);
      string step1Result = E2E.GetLastToolMessage(host.Mock.RequestBodies[3]);
      Assert.Contains("[plan] updated step 1 in #1", step1Result, StringComparison.Ordinal);

      // THE REFUSAL IS THE EVIDENCE, read from the app's own notice rather than inferred.
      string notices = string.Join(" / ", shell.Tabs[0].ViewModel.Transcript.Entries
          .OfType<NoticeEntry>()
          .Select(n => n.Text));
      Assert.Contains("[chain] advance refused [TurnBusy]", notices, StringComparison.Ordinal);

      // AND THE CHAIN STALLS: the cell is still step 1's, and the record never moved on.
      Assert.Equal(step1Id, shell.Tabs[0].Container.RootId);
      Chain stalled = await ReadChainAsync(host.DatabasePath, workspaceId).ConfigureAwait(true);
      Assert.Equal(ChainStatus.Running, stalled.Status);
      Assert.Equal(ChainStepStatus.Pending, stalled.StepAt(2)!.Status);
      Assert.Null(stalled.StepAt(2)!.SessionId);
    }
    finally
    {
      await ShutdownAsync(shell, workspace).ConfigureAwait(true);
    }
  }

  /// <summary>The shell in its PRODUCTION shape (DesktopHost.CreateMainWindow): the factory
  ///     supplies both creation delegates, and <see cref="ChainAdvanceFactory"/> answers the
  ///     mechanical advance per session. No test-only seam is introduced - the delegates are
  ///     the ones the real host builds.</summary>
  private static async Task<MainViewModel> OpenShellAsync(E2E.HostHarness host)
  {
    AgentSettings settings = host.BuildSettings();
    AgentSessionFactory factory = new(settings, new AppDatabase(host.DatabasePath));
    MainViewModel? shellRef = null;
    ChainAdvanceService? AdvanceFor(AgentSession session) =>
        ChainAdvanceFactory.For(session, () => shellRef);
    MainViewModel shell = new(createSession: null, new MainViewModelOptions
    {
      PreferredProviderId = Providers.OpenRouter,
      Settings = settings,
      SessionFactory = factory,
      ChainAdvance = AdvanceFor,
    });
    shellRef = shell;
    await Task.CompletedTask.ConfigureAwait(true);
    return shell;
  }

  /// <summary>Declares the workspace's gate (issue #304). <c>exit 0</c> is portable across the
  ///     PowerShell and cmd fallbacks and always green, so the gate is genuinely satisfied - the
  ///     test ARRANGES it rather than tripping it and looking like a chain failure.</summary>
  private static async Task DeclareGateAsync(AgentSession session)
  {
    IStateService state = session.Services.GetRequiredService<IStateService>();
    Result<StateKeyValue> declared = await state
        .SetAsync(CompletionGateGoalCheckKey, /*lang=json,strict*/ "[\"exit 0\"]", null,
            TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.True(declared.IsSuccess, declared.Error?.Message);
  }

  /// <summary>Closes the shell (which reaps every host process) and the temp workspace.</summary>
  private static async Task ShutdownAsync(MainViewModel? shell, string workspace)
  {
    if (shell is not null)
    {
      await shell.ShutdownAllAsync().ConfigureAwait(true);
    }

    try
    {
      Directory.Delete(workspace, recursive: true);
    }
    catch (IOException)
    {
      // best effort: a still-draining host process may pin the directory
    }
  }

  /// <summary>Runs one turn and waits for it, bounded so a wedged turn fails the test instead of
  ///     hanging the suite (deadlock vigilance).</summary>
  private static async Task RunTurnAsync(AgentSessionViewModel cell, string input, TimeSpan limit)
  {
    await cell.SubmitAsync(input).WaitAsync(limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
    await cell.WaitForTurnAsync().WaitAsync(limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
  }

  /// <summary>Waits for the tab's primary cell to be REPLACED by a different session - the
  ///     in-place swap a chain launch performs. Bounded, and it pumps the dispatcher because the
  ///     session-request handler is posted onto the UI thread by the session view-model.</summary>
  private static async Task<AgentId> WaitForCellSwapAsync(MainViewModel shell, AgentId previous, TimeSpan limit)
  {
    DateTimeOffset deadline = DateTimeOffset.UtcNow + limit;
    while (DateTimeOffset.UtcNow < deadline)
    {
      Dispatcher.UIThread.RunJobs();
      if (shell.Tabs.Count == 1 && shell.Tabs[0].Container.RootId != previous)
      {
        return shell.Tabs[0].Container.RootId;
      }

      await Task.Delay(50, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    // A timeout must say what the APP said, not just that time ran out: a refused launch is the
    // interesting failure here, and its reason lands in the transcript as a notice.
    string said = string.Join(" / ", shell.Tabs.SelectMany(t => t.ViewModel.Transcript.Entries)
        .OfType<NoticeEntry>()
        .Select(n => n.Text));
    throw new TimeoutException(
        $"the tab's cell never advanced past session {previous} within {limit}. "
        + $"The transcript said: {said}");
  }

  /// <summary>The chain record as the SAME workspace store holds it, read over the harness's
  ///     database - so the assertions describe the durable record, not a live object graph.
  ///     </summary>
  private static async Task<Chain> ReadChainAsync(string databasePath, string workspaceId)
  {
    SqliteChainStore store = new(new AppDatabase(databasePath), workspaceId);
    Result<Chain> chain = await store.GetAsync(1, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(chain.IsSuccess, chain.Error?.Message);
    return chain.Value;
  }

  /// <summary>Waits until the durable record satisfies <paramref name="settled"/>, then returns
  ///     it. A launch answers the app BEFORE the reassignment is written - deliberately, so the
  ///     blocked tool never waits on bookkeeping - so a single read can catch a real, correct
  ///     intermediate state. Bounded; it returns the last record it saw so the caller's assertion
  ///     reports what the store actually held.</summary>
  private static async Task<Chain> WaitForRecordAsync(string databasePath, string workspaceId,
      Func<Chain, bool> settled, TimeSpan limit)
  {
    DateTimeOffset deadline = DateTimeOffset.UtcNow + limit;
    Chain last = await ReadChainAsync(databasePath, workspaceId).ConfigureAwait(true);
    while (DateTimeOffset.UtcNow < deadline)
    {
      if (settled(last))
      {
        return last;
      }

      await Task.Delay(50, TestContext.Current.CancellationToken).ConfigureAwait(true);
      last = await ReadChainAsync(databasePath, workspaceId).ConfigureAwait(true);
    }

    return last;
  }

  /// <summary>One step's scripted turn: run the gate, then claim the step done - the two things
  ///     the <c>executing-a-chain</c> skill instructs, in the order it instructs them.</summary>
  private static string StepProgram(int position) => $$"""
      var gate = Tools.Invoke("state.checkgoal", new { timeoutSeconds = 120 });
      var done = Tools.Invoke("plan.update-step", new { timeoutSeconds = 60,
          id = 1, position = {{position}}, status = "Done" });
      return "gate=" + gate + "|" + done;
      """;

  /// <summary>Wraps a program as the OUTER exec call. The budget is generous because
  ///     <c>chain.start</c> blocks on the app launching a host process.</summary>
  private static string ExecProgram(string program) =>
      JsonSerializer.Serialize(new { timeoutSeconds = 300, title = "chain-e2e", program });

  /// <summary>The state key a workspace's gate commands live under (issue #283). Stated here
  ///     rather than reached for through <c>CompletionGate</c> so the test declares the gate
  ///     exactly where the production reader looks for it.</summary>
  private const string CompletionGateGoalCheckKey = "goal/check";
}
