using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;
using eThangAgent.Storage.ACL;
using eThangAgent.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>A two-member group runs together, merges, and is reviewed: the acceptance test for
///     parallel chain steps (docs/parallel-chain-steps.md).
///     <para>WHAT IT DRIVES, for real: a model-authored <c>chain.start</c> declares steps 1 and 2
///     as one group, step 3 as the reconciler, and step 4 as a review. Two member sessions are
///     launched, each anchored at its own git worktree; each member writes a file, commits it,
///     verifies, and marks its step done; the reconciler's brief names both branches and it
///     merges them into the workspace checkout; the review session then starts and the members'
///     worktrees are gone.</para>
///     <para>THE MOCK IS SCRIPTED, AND THAT IS THE HONEST LIMIT. Each session obeys a scripted
///     exec program rather than reading its brief and deciding, so this proves the MACHINERY
///     runs end to end. The briefs are asserted separately, which is what keeps the instruction
///     from being an untested claim.</para>
///     <para>THE MEMBERS SHARE ONE PROGRAM, deliberately: two sessions on one model draw from
///     one scripted queue in arrival order, so a program that assumed which member it was would
///     fail whenever the order flipped. Each member asks git which branch it is on and derives
///     its own position from it.</para></summary>
[Collection("Desktop E2E")]
[Trait("Category", "Process")]
public class ChainGroupExecutionE2ETests
{
  /// <summary>How long one step's turn may take. Generous because a chain launch BLOCKS while
  ///     the app builds a container and launches a host process for the step's session.</summary>
  private static readonly TimeSpan StepLimit = TestCeiling.Value;

  /// <summary>The creating session's turn: a four-step plan, then the explicit plan-to-execution
  ///     transition declaring the group. The database is fresh, so the plan id is 1 and the chain
  ///     id is 1.</summary>
  private const string CreatorProgram = """
      var created = Tools.Invoke("plan.create", new { timeoutSeconds = 60,
          title = "Group E2E", goal = "Prove a group runs, merges, and is reviewed.",
          steps = new[] { new { title = "member one" }, new { title = "member two" },
                          new { title = "merge the branches" }, new { title = "review the merge" } } });
      var started = Tools.Invoke("chain.start", new { timeoutSeconds = 300, planId = 1,
          steps = new object[] {
              new { position = 1, group = 1 },
              new { position = 2, group = 1 },
              new { position = 3 },
              new { position = 4, role = "review", model = "mock/sub-model" } } });
      return created + "|" + started;
      """;

  /// <summary>One group member's scripted turn: it derives its own step position from the branch
  ///     its checkout is on, writes and COMMITS a file, verifies, and marks that step done - the
  ///     order the <c>executing-a-chain</c> skill instructs, with the commit duty a member's
  ///     brief states.
  ///     <para>ONE PROGRAM FOR BOTH MEMBERS, deliberately. Two members draw from one model's
  ///     scripted queue in arrival order, so a program written for a particular member would
  ///     fail whenever the order flipped. Deriving the position from the checkout also proves
  ///     the anchor: the branch is only readable from inside the member's own worktree.</para>
  ///     </summary>
  private const string MemberProgram = """
      var branch = Shell("git", "rev-parse", "--abbrev-ref", "HEAD");
      var name = branch.Stdout.Trim();
      var position = int.Parse(name.Substring(name.Length - 1), System.Globalization.CultureInfo.InvariantCulture);
      var write = Tools.write(new { timeoutSeconds = 60, path = "member-" + position + ".txt",
          content = "work by member " + position });
      var add = Shell("git", "add", "-A");
      var commit = Shell("git", "commit", "-m", "member " + position + " work");
      var verify = Shell("git", "rev-parse", "HEAD");
      var done = Tools.Invoke("plan.update-step", new { timeoutSeconds = 60,
          id = 1, position = position, status = "Done" });
      return "branch=" + name + "|add=" + add.ExitCode + "|commit=" + commit.ExitCode
          + "|verify=" + verify.ExitCode + "|" + done;
      """;

  /// <summary>The reconciler's scripted turn: merge both members' branches into the workspace
  ///     checkout, verify, and mark step 3 done.</summary>
  private const string ReconcilerProgram = """
      var first = Shell("git", "merge", "--no-edit", "worktree/chain-1-step-1");
      var second = Shell("git", "merge", "--no-edit", "worktree/chain-1-step-2");
      var verify = Shell("git", "rev-parse", "HEAD");
      var done = Tools.Invoke("plan.update-step", new { timeoutSeconds = 60,
          id = 1, position = 3, status = "Done" });
      return "first=" + first.ExitCode + "|second=" + second.ExitCode
          + "|verify=" + verify.ExitCode + "|" + done;
      """;

  [AvaloniaFact]
  public async Task Group_RunsTogether_Merges_AndIsReviewed()
  {
#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
    await using E2E.HostHarness host = new();
#pragma warning restore CA2007
    host.ClaimHostsForCurrentTest();
    _ = await host.StartAsync().ConfigureAwait(true);
    string workspace = TestTempRoot.CreateDirectory("ethang-chain-group-e2e").FullName;
    MainViewModel? shell = null;
    try
    {
      SeedRepository(workspace);
      shell = await OpenShellAsync(host).ConfigureAwait(true);
      // THE SCRIPT IS ADDRESSED BY WHAT EACH REQUEST SAYS, not by which session asked: the two
      // members share one model and one queue, so a queue would hand whichever member asked
      // first the answer written for the other. Each member's brief names its own position, and
      // that is what selects its turn.
      // THE CREATOR IS KEYED ON ITS OWN USER MESSAGE: it is not a chain step, so it carries no
      // brief, and a marker taken from a brief would match nothing and leave it answering in
      // prose without ever calling chain.start.
      _ = host.Mock.ReturnsForRequestContaining("plan the work and start executing it",
          E2E.ExecToolCall("creator_1", ExecProgram(CreatorProgram)));
      _ = host.Mock.ReturnsForRequestContaining("Mark step 1 of plan #1 done",
          E2E.ExecToolCall("member_1", ExecProgram(MemberProgram)));
      _ = host.Mock.ReturnsForRequestContaining("Mark step 2 of plan #1 done",
          E2E.ExecToolCall("member_2", ExecProgram(MemberProgram)));
      // THE RECONCILER IS KEYED ON ITS OWN BRIEF, scripted up front: the rule waits for a request
      // that says it, so nothing has to be scripted mid-run and race the turn it answers.
      _ = host.Mock.ReturnsForRequestContaining("You are the RECONCILER",
          E2E.ExecToolCall("reconciler_1", ExecProgram(ReconcilerProgram)));
      // EVERY SECOND TURN IS PROSE: once a session has run its program it only has to finish,
      // and these are served in arrival order because they say nothing about who asked.
      _ = host.Mock.ReturnsForModel(E2E.SessionModel,
          E2E.RawCompletion("chain started"),
          E2E.RawCompletion("member done"),
          E2E.RawCompletion("member done"),
          E2E.RawCompletion("merged"));
      // The review step runs on its own model, which is what keeps it separate from the
      // implementers and what lets the mock serve it without racing the members' queue.
      _ = host.Mock.ReturnsForModel("mock/sub-model", E2E.RawCompletion("reviewed"));

      Result<AgentTabViewModel> opened = await shell.OpenAgentAsync(workspace, Providers.OpenRouter)
          .ConfigureAwait(true);
      Assert.True(opened.IsSuccess, opened.Error?.Message);
      AgentSession creator = opened.Value.Container;
      AgentId creatorId = creator.RootId;
      string workspaceId = creator.Services.GetRequiredService<IWorkspaceContext>().WorkspaceId;

      // ---- BOUNDARY 1: chain.start declares the group and both members start together ----
      await RunTurnAsync(shell.Tabs[0].ViewModel, "plan the work and start executing it", StepLimit)
          .ConfigureAwait(true);
      string startResult = E2E.GetLastToolMessage(host.Mock.RequestBodies[1]);
      Assert.Contains("[chain] created #1 from plan #1", startResult, StringComparison.Ordinal);

      // ---- BOUNDARY 2: TWO sessions, each anchored at its own worktree, both Running ----
      Chain grouped = await WaitForRecordAsync(host.DatabasePath, workspaceId,
          c => c.StepAt(1)?.Status == ChainStepStatus.Running
              && c.StepAt(2)?.Status == ChainStepStatus.Running
              && IsRealSession(c.StepAt(1)) && IsRealSession(c.StepAt(2))
              && c.StepAt(1)?.WorktreePath is not null
              && c.StepAt(2)?.WorktreePath is not null, StepLimit, () => Notices(shell))
          .ConfigureAwait(true);

      Assert.Equal(ChainStepStatus.Running, grouped.StepAt(1)!.Status);
      Assert.Equal(ChainStepStatus.Running, grouped.StepAt(2)!.Status);
      Assert.Equal(1, grouped.GroupAt(1));
      Assert.Equal(1, grouped.GroupAt(2));
      Assert.Null(grouped.GroupAt(3));
      // EACH MEMBER RUNS IN ITS OWN CHECKOUT, under the workspace's .worktrees directory.
      string memberOne = grouped.StepAt(1)!.WorktreePath!;
      string memberTwo = grouped.StepAt(2)!.WorktreePath!;
      Assert.NotEqual(memberOne, memberTwo, StringComparer.OrdinalIgnoreCase);
      Assert.StartsWith(Path.Combine(workspace, ".worktrees"), memberOne, StringComparison.OrdinalIgnoreCase);
      Assert.StartsWith(Path.Combine(workspace, ".worktrees"), memberTwo, StringComparison.OrdinalIgnoreCase);
      Assert.True(Directory.Exists(memberOne), memberOne + " was recorded but does not exist.");
      Assert.True(Directory.Exists(memberTwo), memberTwo + " was recorded but does not exist.");
      // AND THE SESSIONS ARE THE ONES THE RECORD NAMES, each a cell of its own.
      AgentId memberOneId = new(Guid.Parse(grouped.StepAt(1)!.SessionId!));
      AgentId memberTwoId = new(Guid.Parse(grouped.StepAt(2)!.SessionId!));
      Assert.NotEqual(memberOneId, memberTwoId);
      Assert.NotEqual(creatorId, memberOneId);
      Assert.NotEqual(creatorId, memberTwoId);

      // ---- BOUNDARY 3: the reconciler starts once both members settled, and knows the branches ----
      Chain reconciled = await WaitForRecordAsync(host.DatabasePath, workspaceId,
          c => c.StepAt(3)?.Status == ChainStepStatus.Running && IsRealSession(c.StepAt(3)),
          StepLimit, () => Notices(shell)).ConfigureAwait(true);
      Assert.Equal(ChainStepStatus.Done, reconciled.StepAt(1)!.Status);
      Assert.Equal(ChainStepStatus.Done, reconciled.StepAt(2)!.Status);
      Assert.Equal(ChainStepStatus.Running, reconciled.StepAt(3)!.Status);
      string branchOne = reconciled.StepAt(1)!.WorktreeBranch!;
      string branchTwo = reconciled.StepAt(2)!.WorktreeBranch!;
      Assert.Equal("worktree/chain-1-step-1", branchOne, StringComparer.Ordinal);
      Assert.Equal("worktree/chain-1-step-2", branchTwo, StringComparer.Ordinal);
      AgentId reconcilerId = new(Guid.Parse(reconciled.StepAt(3)!.SessionId!));
      string reconcilerBrief = BriefOf(shell, reconcilerId);
      Assert.Contains("## You are the RECONCILER", reconcilerBrief, StringComparison.Ordinal);
      Assert.Contains(branchOne, reconcilerBrief, StringComparison.Ordinal);
      Assert.Contains(branchTwo, reconcilerBrief, StringComparison.Ordinal);

      // ---- BOUNDARY 4: the merge landed both members' work in the workspace checkout ----
      await WaitForFileAsync(Path.Combine(workspace, "member-1.txt"), StepLimit, () => Notices(shell))
          .ConfigureAwait(true);
      await WaitForFileAsync(Path.Combine(workspace, "member-2.txt"), StepLimit, () => Notices(shell))
          .ConfigureAwait(true);

      // ---- BOUNDARY 5: the review starts, and the landed group's worktrees are gone ----
      Chain reviewed = await WaitForRecordAsync(host.DatabasePath, workspaceId,
          c => c.StepAt(4)?.Status == ChainStepStatus.Running && IsRealSession(c.StepAt(4)),
          StepLimit, () => Notices(shell)).ConfigureAwait(true);
      Assert.Equal(ChainStepStatus.Done, reviewed.StepAt(3)!.Status);
      Assert.Equal(ChainStepStatus.Running, reviewed.StepAt(4)!.Status);
      // THE RECORD STOPS NAMING A CHECKOUT THAT NO LONGER EXISTS, so no later reader launches
      // a session into a directory the landing removed.
      Assert.Null(reviewed.StepAt(1)!.WorktreePath);
      Assert.Null(reviewed.StepAt(2)!.WorktreePath);
      Assert.False(Directory.Exists(memberOne), memberOne + " outlived the landing.");
      Assert.False(Directory.Exists(memberTwo), memberTwo + " outlived the landing.");
      AgentId reviewId = new(Guid.Parse(reviewed.StepAt(4)!.SessionId!));
      Assert.NotNull(shell.Tabs[0].FindCell(reviewId));
      Assert.Equal("step 4 of 4 \u00b7 Group E2E", shell.Tabs[0].FindCell(reviewId)!.PlacementLine);
    }
    finally
    {
      await ShutdownAsync(shell, workspace).ConfigureAwait(true);
    }
  }

  /// <summary>The shell in its PRODUCTION shape: the factory supplies both creation delegates,
  ///     and <see cref="ChainAdvanceFactory"/> answers the mechanical advance per session.</summary>
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

  /// <summary>Closes the shell (which reaps every host process) and the temp workspace.</summary>
  private static async Task ShutdownAsync(MainViewModel? shell, string workspace)
  {
    if (shell is not null)
    {
      // BOUNDED, because a wedged teardown is a failure this test must REPORT rather than
      // absorb: an unbounded shutdown is what turns one stuck session into a suite-wide hang.
      await shell.ShutdownAllAsync().WaitAsync(StepLimit, TestContext.Current.CancellationToken)
          .ConfigureAwait(true);
    }

    RemoveRepository(workspace);
  }

  /// <summary>True when the step holds a REAL session id. The advance claims a step under a
  ///     <c>pending-</c> placeholder and reassigns it to the launched session's id afterwards, so
  ///     a wait that accepted any non-null id would read the claim as the launch.</summary>
  /// <param name="step">The step to read, or null.</param>
  private static bool IsRealSession(ChainStep? step) =>
      step?.SessionId is { } id && Guid.TryParse(id, out _);

  /// <summary>Everything the shell's cells said, joined into one line - what the app said when a
  ///     wait timed out. Notices alone miss a step whose own turn failed, which is the case a
  ///     group's member presents: its refusal lands in ITS transcript, not the tab's.</summary>
  private static string Notices(MainViewModel shell) =>
      $"cells={shell.Tabs.Sum(tab => tab.Cells.Count)}; " +
      string.Join(" / ", shell.Tabs.SelectMany(tab => tab.Cells)
          .SelectMany(cell => cell.ViewModel.Transcript.Entries)
          .Select(entry => entry switch
          {
            NoticeEntry notice => "notice: " + notice.Text,
            ToolResultEntry result => "result: " + result.Summary + " :: " + result.FullContent,
            AssistantTextEntry assistant => "assistant: " + assistant.Text,
            ToolCallEntry call => "call: " + call.Name,
            CommandRunEntry command => "command: " + command.Command,
            _ => null,
          })
          .Where(text => !string.IsNullOrWhiteSpace(text)))
      + " / CELLS: " + string.Join(" | ", shell.Tabs.SelectMany(tab => tab.Cells)
          .Select(cell => $"{(cell.IsFinished ? "finished" : "live")}:{cell.RootId.ToString()[..8]}:"
              + string.Join(",", cell.ViewModel.Transcript.Entries.Select(entry => entry.GetType().Name))));

  /// <summary>The brief a session was seeded with: the first system entry carrying the chain
  ///     brief's prefix. This is what the model would read, so asserting on it is asserting on
  ///     the instruction rather than on the record that produced it.</summary>
  private static string BriefOf(MainViewModel shell, AgentId sessionId)
  {
    AgentCellViewModel cell = shell.Tabs[0].FindCell(sessionId)
        ?? throw new InvalidOperationException($"no cell for session {sessionId}.");
    SystemMessageEntry brief = cell.ViewModel.Transcript.Entries
        .OfType<SystemMessageEntry>()
        .FirstOrDefault(entry => entry.Text.StartsWith(ChainBriefRenderer.Prefix, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"session {sessionId} was seeded with no chain brief.");
    return brief.Text;
  }

  /// <summary>Runs one turn and waits for it, bounded so a wedged turn fails the test instead of
  ///     hanging the suite (deadlock vigilance).</summary>
  private static async Task RunTurnAsync(AgentSessionViewModel cell, string input, TimeSpan limit)
  {
    await cell.SubmitAsync(input).WaitAsync(limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
    await cell.WaitForTurnAsync().WaitAsync(limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
  }

  /// <summary>The chain record as the SAME workspace store holds it, read over the harness's
  ///     database - so the assertions describe the durable record, not a live object graph.</summary>
  private static async Task<Chain> ReadChainAsync(string databasePath, string workspaceId)
  {
    SqliteChainStore store = new(new AppDatabase(databasePath), workspaceId);
    Result<Chain> chain = await store.GetAsync(1, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(chain.IsSuccess, chain.Error?.Message);
    return chain.Value;
  }

  /// <summary>Waits until the durable record satisfies <paramref name="settled"/>, then returns
  ///     it. A launch answers the app BEFORE the reassignment is written, so a single read can
  ///     catch a real, correct intermediate state. Bounded; it returns the last record it saw so
  ///     the caller's assertion reports what the store actually held.</summary>
  private static async Task<Chain> WaitForRecordAsync(string databasePath, string workspaceId,
      Func<Chain, bool> settled, TimeSpan limit, Func<string>? diagnosed = null)
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
      Dispatcher.UIThread.RunJobs();
      last = await ReadChainAsync(databasePath, workspaceId).ConfigureAwait(true);
    }

    // A TIMEOUT MUST SAY WHAT THE APP SAID, not only that time ran out: a refused completion is
    // the interesting failure here, and its reason lands in the step's own transcript.
    return diagnosed is null
      ? last
      : throw new TimeoutException($"the chain never reached the expected state within {limit}. "
          + $"The record held: {Describe(last)}. The app said: {diagnosed()}.");
  }

  /// <summary>The record as one line, so a timeout names the state it actually reached.</summary>
  private static string Describe(Chain chain) =>
      $"status={chain.Status} steps=["
      + string.Join(", ", chain.Steps.Select(step =>
          $"{step.Position}:{step.Status}{(step.Group is { } g ? "/g" + g : "")}"))
      + "]";

  /// <summary>Waits until <paramref name="path"/> exists, bounded. The merge happens inside the
  ///     reconciler's turn, so its result arrives after the record already names the session.
  ///     </summary>
  private static async Task WaitForFileAsync(string path, TimeSpan limit, Func<string> diagnosed)
  {
    DateTimeOffset deadline = DateTimeOffset.UtcNow + limit;
    while (DateTimeOffset.UtcNow < deadline && !File.Exists(path))
    {
      await Task.Delay(50, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    Assert.True(File.Exists(path), $"the merge never landed {path}. The app said: {diagnosed()}.");
  }

  /// <summary>A git repository in the test workspace holding a buildable project, so a member's
  ///     verification is a real <c>dotnet build</c> that exits 0. The <c>.gitignore</c> is what
  ///     keeps a build's output out of <c>git status</c>: a member's worktree must read CLEAN
  ///     after its commit, or its completion is refused for work it did commit.</summary>
  private static void SeedRepository(string workspace)
  {
    RunGit(workspace, "init");
    RunGit(workspace, "config", "user.email", "test@test.com");
    RunGit(workspace, "config", "user.name", "Test");
    File.WriteAllText(Path.Combine(workspace, ".gitignore"), "bin/\nobj/\n.worktrees/\n");
    File.WriteAllText(Path.Combine(workspace, "chain-e2e.csproj"),
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n"
        + "  <PropertyGroup>\n"
        + "    <TargetFramework>net10.0</TargetFramework>\n"
        + "  </PropertyGroup>\n"
        + "</Project>\n");
    File.WriteAllText(Path.Combine(workspace, "Program.cs"),
        "internal static class Program\n{\n  private static void Main()\n  {\n  }\n}\n");
    RunGit(workspace, "add", "-A");
    RunGit(workspace, "commit", "-m", "base");
  }

  /// <summary>Removes the temp repository. A worktree still registered with git keeps its own
  ///     metadata alive, so the administrative entries are pruned first; the rest is best effort
  ///     because a draining host process may pin a directory.</summary>
  private static void RemoveRepository(string workspace)
  {
    try
    {
      RunGit(workspace, "worktree", "prune");
      Directory.Delete(workspace, recursive: true);
    }
    catch (IOException)
    {
      // best effort: a still-draining host process may pin the directory
    }
    catch (UnauthorizedAccessException)
    {
      // best effort: a read-only git object file may refuse deletion
    }
  }

  /// <summary>Runs one git command in <paramref name="workspace"/> and fails the test when it
  ///     exits non-zero: a fixture that silently did nothing would make every later assertion
  ///     describe a repository that was never made.</summary>
  private static void RunGit(string workspace, params string[] args)
  {
    System.Diagnostics.ProcessStartInfo info = new("git")
    {
      WorkingDirectory = workspace,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    foreach (string argument in args)
    {
      info.ArgumentList.Add(argument);
    }

    using System.Diagnostics.Process git = System.Diagnostics.Process.Start(info)!;
    string error = git.StandardError.ReadToEnd();
    _ = git.WaitForExit(TestCeiling.Value);
    Assert.True(git.ExitCode == 0, $"git {string.Join(' ', args)} exited {git.ExitCode}: {error}");
  }

  /// <summary>Wraps a program as the OUTER exec call. The budget is generous because
  ///     <c>chain.start</c> blocks on the app launching a host process.</summary>
  private static string ExecProgram(string program) =>
      System.Text.Json.JsonSerializer.Serialize(new { timeoutSeconds = 300, title = "chain-group-e2e", program });
}
