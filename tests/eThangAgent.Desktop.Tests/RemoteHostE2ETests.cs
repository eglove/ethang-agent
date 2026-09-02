using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.Desktop.Streaming;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>Full-app remote-mode E2E (handoff item 3): the REAL Desktop composition
///     with SubAgent:RemoteHost=true — session open through the factory (attach + exact
///     orphan repair), a child spawned through the production agent.spawn surface that
///     runs IN THE CHILDHOST PROCESS and settles through the wire, the app-side
///     container killed (the host survives), a fresh session open over the same
///     database, and the child's outcome retrievable with orphan repair marking
///     exactly the right rows.</summary>
[Collection("Desktop E2E")]
public class RemoteHostE2ETests
{
  private static string RawCompletion(string content) =>
      System.Text.Json.JsonSerializer.Serialize(
          new { choices = new[] { new { message = new { content } } } });

  private const string PollThenResult = """
            var deadline = System.DateTime.UtcNow.AddSeconds(60);
            var status = Tools.Invoke("agent.status", new { timeoutSeconds = 60, id = "{{child_id}}" });
            while (!status.Contains("status=completed") && !status.Contains("status=failed"))
            {
                if (System.DateTime.UtcNow > deadline)
                    return "poll-timeout; last status: " + status;
                await System.Threading.Tasks.Task.Delay(50);
                status = Tools.Invoke("agent.status", new { timeoutSeconds = 60, id = "{{child_id}}" });
            }
            return Tools.Invoke("agent.result", new { timeoutSeconds = 60, id = "{{child_id}}" });
            """;

  [Fact]
  public async Task RemoteSpawn_SettlesThroughWire_SurvivesAppContainerDeath()
  {
    using E2E.HostHarness host = new();
    _ = await host.StartAsync();

    // Open the session through the REAL factory with RemoteHost=true: the launch of
    // the ChildHost process, attach, and orphan repair all run the production path.
    string ws = Directory.CreateTempSubdirectory("ethang-remote-ws").FullName;
    AgentSessionFactory factory = new(
        host.BuildSettings(remoteHost: true),
        new AppDatabase(host.DatabasePath));
    Result<AgentSession> opened = await factory.CreateAsync(
        ws, Providers.OpenRouter, new NeverAsk(),
        ct: TestContext.Current.CancellationToken);
    Assert.True(opened.IsSuccess, opened.Error?.Message);
    AgentSession session = opened.Value;

    // Pin the session model like the standard harness: selection must not consume the
    // parent's scripted responses.
    session.Services.GetRequiredService<SessionModelPreferences>().ModelId = E2E.SessionModel;

    // Shell surface over the factory-built session (production wiring).
    AgentSessionViewModel? vmRef = null;
    async Task Sink(UiStreamEvent evt) =>
        await (vmRef ?? throw new InvalidOperationException("sink before view-model init"))
            .ApplyUiStreamEventAsync(evt).ConfigureAwait(true);
    MainViewModel shell = await MainViewModel.ForPrebuiltSessionAsync(session, Sink);
    AgentSessionViewModel vm = shell.Tabs[0].ViewModel;
    vmRef = vm;

    // Parent script: spawn through the production surface (exec -> agent.spawn), then
    // poll-to-settled and fetch the report. Child script under the per-spawn model.
    string spawnProgram = "var spawned = Tools.Invoke(\"agent.spawn\", new { timeoutSeconds = 60, taskPrompt = \"Say remote report done and nothing else.\", model = \"mock/sub-model\", label = \"remote-e2e\" }); return spawned;";
    _ = host.Mock.ReturnsForModel(E2E.SessionModel,
        E2E.ExecToolCall("parent_call_1", E2E.ExecProgram(spawnProgram)),
        E2E.ExecToolCall("parent_call_2", E2E.ExecProgram(PollThenResult)),
        RawCompletion("done: remote child reported"));
    _ = host.Mock.ReturnsForModel("mock/sub-model",
        RawCompletion("remote report done"));

    await vm.RunTurnAsync("delegate to a remote child")
        .WaitAsync(TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken);

    // The child settled HOST-side through the wire: its report reached the parent as
    // the poll-and-fetch exec result — the tool message carries it verbatim.
    Assert.Contains("remote report done",
        E2E.FindToolMessageContaining(host.Mock.RequestBodies, "remote report done"),
        StringComparison.OrdinalIgnoreCase);

    // Kill the APP-SIDE container (not the host): exactly the app-death simulation.
    await session.Services.DisposeAsync();

    // Re-open over the SAME database: the factory runs orphan repair with the host's
    // declared (now empty) live set.
    Result<AgentSession> reopened = await factory.CreateAsync(
        ws, Providers.OpenRouter, new NeverAsk(),
        ct: TestContext.Current.CancellationToken);
    Assert.True(reopened.IsSuccess, reopened.Error?.Message);

    // The child's outcome is retrievable from the shared store after the re-open.
    SqliteAgentStore verify = new(new AppDatabase(host.DatabasePath));
    Result<IReadOnlyList<AgentRecord>> all =
        await verify.ListAllAsync(TestContext.Current.CancellationToken);
    Assert.True(all.IsSuccess);
    AgentRecord child = Assert.Single(
        all.Value, r => r.Depth == 1 && r.Label == "remote-e2e");
    Assert.Equal(AgentStatus.Completed, child.Status);
    Assert.Contains("remote report done", child.FinalReport ?? string.Empty, StringComparison.Ordinal);

    // Orphan repair marked exactly the right rows: the settled child (owned by nobody
    // but already terminal) is untouched — still Completed with its report, never
    // re-marked Interrupted; the re-opened session's own root stays Running (exempt).
    Assert.Equal(AgentStatus.Completed, child.Status);
    AgentRecord reopenedRoot = Assert.Single(all.Value, r => r.Depth == 0 && r.Id == reopened.Value.RootId);
    Assert.Equal(AgentStatus.Running, reopenedRoot.Status);

    await reopened.Value.Services.DisposeAsync();
  }

  private sealed class NeverAsk : IClarifyChannel
  {
    public Task<Result<string>> AskAsync(
        ClarifyQuestion question, CancellationToken ct = default)
      => Task.FromResult(Result.Failure<string>(
          new DomainError("Cancelled", "no clarify in remote E2E")));
  }
}
