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
using eThangAgent.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>A chain step APPENDS to the plan it is executing, and the chain carries on. A chain
///     binds its claims to plan step POSITIONS, so the structure lock refuses any edit that would
///     renumber them - and a step landing past every position the chain holds renumbers none.
///     <para>THE SHAPE THIS PINS is the batch pipeline's hinge: a step reads the real work count
///     and then builds the rest of the chain from it, which is impossible if the append is
///     refused. The step here appends through the production capability door over the real
///     composition, then completes itself; the chain must reach Completed with the appended step
///     in the record.</para>
///     <para>WHAT IT LEAVES UNTESTED: the mock is scripted, so this proves the MACHINERY runs,
///     not that a model would decide to append.</para></summary>
[Collection("Desktop E2E")]
[Trait("Category", "Process")]
public class ChainStepAppendE2ETests
{
  private static readonly TimeSpan Limit = TestCeiling.Value;

  /// <summary>The creating session: one step, then the explicit plan-to-execution transition.
  ///     The database is fresh, so the plan id is deterministically 1.</summary>
  private const string CreatorProgram = """
      var created = Tools.Invoke("plan.create", new { timeoutSeconds = 60,
          title = "Append E2E", goal = "Prove a chain step can append to its own plan.",
          steps = new[] { new { title = "hinge" } } });
      var started = Tools.Invoke("chain.start", new { timeoutSeconds = 300, planId = 1 });
      return created + "|" + started;
      """;

  /// <summary>The hinge's turn: append a step past the chain's reach, append one INSIDE it, then
  ///     verify and complete itself. The refusal is asserted here rather than in a unit test
  ///     because the position the chain holds is only real once the composition answers it.</summary>
  private const string HingeProgram = """
      var appended = Tools.Invoke("plan.add-step", new { timeoutSeconds = 60,
          id = 1, title = "appended by the hinge" });
      var inside = Tools.Invoke("plan.add-step", new { timeoutSeconds = 60,
          id = 1, title = "inside the chain's range", position = 1 });
      var verify = Shell("dotnet", "build", "--nologo", "-v", "q");
      var done = Tools.Invoke("plan.update-step", new { timeoutSeconds = 60,
          id = 1, position = 1, status = "Done" });
      return appended + "|" + inside + "|" + verify.ExitCode + "|" + done;
      """;

  [AvaloniaFact]
  public async Task AChainStep_AppendsPastTheChainsReach_AndTheChainCarriesOn()
  {
#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
    await using E2E.HostHarness host = new();
#pragma warning restore CA2007
    host.ClaimHostsForCurrentTest();
    _ = await host.StartAsync().ConfigureAwait(true);
    string workspace = TestTempRoot.CreateDirectory("ethang-chain-append-e2e").FullName;
    MainViewModel? shell = null;
    try
    {
      SeedVerifiableProject(workspace);
      shell = await OpenShellAsync(host).ConfigureAwait(true);
      _ = host.Mock.ReturnsForModel(E2E.SessionModel,
          E2E.ExecToolCall("creator_1", ExecProgram(CreatorProgram)),
          E2E.RawCompletion("chain started"),
          E2E.ExecToolCall("hinge_1", ExecProgram(HingeProgram)),
          E2E.RawCompletion("hinge done"));

      Result<AgentTabViewModel> opened = await shell.OpenAgentAsync(workspace, Providers.OpenRouter).ConfigureAwait(true);
      Assert.True(opened.IsSuccess, opened.Error?.Message);
      AgentSession creator = opened.Value.Container;
      AgentId creatorId = creator.RootId;
      string workspaceId = creator.Services.GetRequiredService<IWorkspaceContext>().WorkspaceId;

      await RunTurnAsync(shell.Tabs[0].ViewModel, "plan the work and start executing it", Limit).ConfigureAwait(true);
      AgentId hingeId = await WaitForNewCellAsync(shell, [creatorId], Limit).ConfigureAwait(true);
      Assert.NotEqual(creatorId, hingeId);

      // THE HINGE'S OWN TURN: the mock serves it, so the append happens inside a real chain step.
      Chain finished = await WaitForRecordAsync(host.DatabasePath, workspaceId,
          c => c.Status == ChainStatus.Completed && c.FinalSessionId is not null, Limit).ConfigureAwait(true);
      Assert.Equal(ChainStatus.Completed, finished.Status);
      Assert.Equal(ChainStepStatus.Done, finished.StepAt(1)!.Status);

      // AND THE APPEND LANDED: the plan the chain is executing now holds the appended step.
      SqlitePlanStore plans = new(new AppDatabase(host.DatabasePath), workspaceId);
      Result<PlanDomain.Plan> plan = await plans.GetAsync(1, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(plan.IsSuccess, plan.Error?.Message);
      Assert.Equal(2, plan.Value.Steps.Count);
      Assert.Equal("appended by the hinge", plan.Value.Steps[1].Title);
    }
    finally
    {
      await ShutdownAsync(shell, workspace).ConfigureAwait(true);
    }
  }

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

  private static async Task RunTurnAsync(AgentSessionViewModel cell, string input, TimeSpan limit)
  {
    await cell.SubmitAsync(input).WaitAsync(limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
    await cell.WaitForTurnAsync().WaitAsync(limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
  }

  private static async Task<AgentId> WaitForNewCellAsync(MainViewModel shell,
      IReadOnlyList<AgentId> known, TimeSpan limit)
  {
    DateTimeOffset deadline = DateTimeOffset.UtcNow + limit;
    while (DateTimeOffset.UtcNow < deadline)
    {
      Dispatcher.UIThread.RunJobs();
      if (shell.Tabs.Count == 1)
      {
        AgentCellViewModel? added = shell.Tabs[0].Cells
            .Skip(1)
            .FirstOrDefault(cell => !known.Contains(cell.RootId));
        if (added is not null)
        {
          return added.RootId;
        }
      }

      await Task.Delay(50, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    string said = string.Join(" / ", shell.Tabs.SelectMany(t => t.Cells)
        .SelectMany(c => c.ViewModel.Transcript.Entries)
        .OfType<NoticeEntry>()
        .Select(n => n.Text));
    throw new TimeoutException(
        $"the tab never gained a cell for a session outside {known.Count} known one(s) "
        + $"within {limit}. The transcript said: {said}");
  }

  private static async Task<Chain> WaitForRecordAsync(string databasePath, string workspaceId,
      Func<Chain, bool> settled, TimeSpan limit)
  {
    SqliteChainStore store = new(new AppDatabase(databasePath), workspaceId);
    DateTimeOffset deadline = DateTimeOffset.UtcNow + limit;
    Result<Chain> chain = await store.GetAsync(1, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(chain.IsSuccess, chain.Error?.Message);
    while (DateTimeOffset.UtcNow < deadline)
    {
      if (settled(chain.Value))
      {
        return chain.Value;
      }

      await Task.Delay(50, TestContext.Current.CancellationToken).ConfigureAwait(true);
      chain = await store.GetAsync(1, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(chain.IsSuccess, chain.Error?.Message);
    }

    return chain.Value;
  }

  private static void SeedVerifiableProject(string workspace)
  {
    File.WriteAllText(Path.Combine(workspace, "chain-append-e2e.csproj"),
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n"
        + "  <PropertyGroup>\n"
        + "    <TargetFramework>net10.0</TargetFramework>\n"
        + "  </PropertyGroup>\n"
        + "</Project>\n");
    File.WriteAllText(Path.Combine(workspace, "Program.cs"),
        "internal static class Program\n{\n  private static void Main()\n  {\n  }\n}\n");
  }

  private static string ExecProgram(string program) =>
      JsonSerializer.Serialize(new { timeoutSeconds = 300, title = "chain-append-e2e", program });
}
