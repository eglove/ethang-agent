using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>W2.5 restart-survival E2E through the REAL composed core: a link consented in a
///     session whose container is disposed is resolved by a later session over the SAME
///     database — the hydrated registry keeps agent.route's vocabulary alive across restarts.
///     Honesty case first (persisted target gone => NotRunning through agent.route verbatim);
///     then consent re-points the name (replace-by-name) to a real child of session two and
///     agent.route delivers while the child is parked mid-tool; the child drains the steering
///     at its next safe point and the text reaches the provider. Deterministic: the child's
///     first exec call parks on a marker file the test releases AFTER the delivery receipt.
///     Every await is bounded (deadlock vigilance); the marker poll is rig-side, not src.</summary>
[Collection("Desktop E2E")]
public class LinkRestartE2ETests
{
  private static string RawCompletion(string content) =>
      JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });

  [Fact]
  public async Task Link_Consented_In_Session_One_Resolves_And_Delivers_In_Session_Two()
  {
    string dbPath = Path.Combine(Path.GetTempPath(), $"ethang-linkrestart-{Guid.NewGuid():N}.db");
    string marker = Path.Combine(Path.GetTempPath(), "ethang-linkrestart-marker-" + Guid.NewGuid().ToString("N"));
    Guid deadTarget = Guid.NewGuid();
    File.Delete(marker); // park begins cleared; the test releases it
    try
    {
      // ---- Session one: the consent door runs on a real composed container, then the
      // container is disposed — every in-memory link dies with it.
      ServiceProvider first = new ServiceCollection()
          .AddEThangAgentCore(
              new AgentSettings(
                  new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
                  new ZaiSettings(null, new Uri("https://zai.test")),
                  new SubAgentOptions(null, 2)),
              Providers.OpenRouter,
              ModelConfig.Create(E2E.SessionModel, null, 32 * 1024, 0.7f, 32 * 1024).Value!,
              new AgentHostOptions(
                  new SilentChannel(),
                  new FixedWorkspaceContext("app"),
                  new UnrootedPathResolver()))
          .BuildServiceProvider();
      Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", dbPath);
      try
      {
        AgentLinkRegistry firstRegistry = first.GetRequiredService<AgentLinkRegistry>();
        Result<LinkAddress> consented = firstRegistry.Link("researcher", "container-a", deadTarget.ToString("D"), consented: true);
        Assert.True(consented.IsSuccess);
      }
      finally
      {
        await first.DisposeAsync().ConfigureAwait(true);
      }

      // ---- Session two over the SAME database: hydration makes the link resolvable.
      using E2E.HostHarness second = new();
      _ = await second.StartAsync(dbPath);
      AgentLinkRegistry registry = second.Shell.Tabs[0].Container.Services
          .GetRequiredService<AgentLinkRegistry>();
      Result<LinkAddress> hydrated = registry.Resolve("researcher");
      Assert.True(hydrated.IsSuccess);
      Assert.Equal(deadTarget.ToString("D"), hydrated.Value.AgentAddress);

      // ---- Honesty case (BDD): the persisted target no longer exists. The failure reads
      // exactly as an in-session NotRunning does, through the real agent.route action.
      string notRunningProbe =
          """
          return Tools.Invoke("route", new { timeoutSeconds = 60, name = "researcher", text = "status check" });
          """;
      _ = second.Mock.ReturnsForModel(E2E.SessionModel,
          E2E.ExecToolCall("p_call_0", E2E.ExecProgram(notRunningProbe)),
          RawCompletion("the link is cold"));
      await second.Vm.RunTurnAsync("try the persisted link");
      List<string> parentBodies = [.. second.Mock.RequestBodies
          .Where(body => MockOpenRouterServer.TryGetRequestModel(body) == E2E.SessionModel)];
      Assert.True(parentBodies.Count >= 2, $"expected 2 parent requests, got {parentBodies.Count}");
      Assert.Contains("Error [NotRunning]", E2E.GetLastToolMessage(parentBodies[1]), StringComparison.Ordinal);

      // ---- Spawn a real child of session two. Its first exec call PARKS on the marker
      // file, holding the run open so the route below lands in a live mailbox.
      const string spawnScript =
          """
          return Tools.Invoke("agent.spawn", new { timeoutSeconds = 60, taskPrompt = "Child echoes steering and reports.", model = "mock/sub-model", label = "link-e2e" });
          """;
      _ = second.Mock.ReturnsForModel(E2E.SessionModel,
          E2E.ExecToolCall("p_call_1", E2E.ExecProgram(spawnScript)),
          RawCompletion("spawning"));
      string parkedExec =
          """
          var deadline = System.DateTime.UtcNow.AddSeconds(30);
          while (!System.IO.File.Exists(@"MARKER"))
          {
              if (System.DateTime.UtcNow > deadline) return "park-timeout";
              await System.Threading.Tasks.Task.Delay(50);
          }
          return "released";
          """;
      parkedExec = parkedExec.Replace("MARKER", marker, StringComparison.Ordinal);
      _ = second.Mock.ReturnsForModel("mock/sub-model",
          E2E.ExecToolCall("child_call_1", E2E.ExecProgram(parkedExec)),
          RawCompletion("child done"));
      await second.Vm.RunTurnAsync("spawn the helper");
      parentBodies = [.. second.Mock.RequestBodies
          .Where(body => MockOpenRouterServer.TryGetRequestModel(body) == E2E.SessionModel)];

      // Wait (bounded) for the child to reach its parked exec call.
      DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
      while (!second.Mock.RequestBodies.Any(body => MockOpenRouterServer.TryGetRequestModel(body) == "mock/sub-model")
          && DateTimeOffset.UtcNow < deadline)
      {
        await Task.Delay(50, TestContext.Current.CancellationToken).ConfigureAwait(true);
      }

      Assert.True(parentBodies.Count >= 4, "parent lost its spawn receipt");
      string spawnReceipt = E2E.GetLastToolMessage(parentBodies[3]);
      Assert.Matches("^id=[0-9a-fA-F-]{36} status=running$", spawnReceipt.Trim());
      Guid childId = Guid.Parse(spawnReceipt[3..39]);

      // ---- Consent re-points "researcher" to the live child (replace-by-name).
      Result<LinkAddress> repointed = registry.Link("researcher", "container-a", childId.ToString("D"), consented: true);
      Assert.True(repointed.IsSuccess);

      // ---- Route through the link while the child is parked mid-tool.
      const string routeScript =
          """
          return Tools.Invoke("route", new { timeoutSeconds = 60, name = "researcher", text = "hello from the root" });
          """;
      _ = second.Mock.ReturnsForModel(E2E.SessionModel,
          E2E.ExecToolCall("p_call_2", E2E.ExecProgram(routeScript)),
          RawCompletion("routed"));
      await second.Vm.RunTurnAsync("deliver through the link");
      parentBodies = [.. second.Mock.RequestBodies
          .Where(body => MockOpenRouterServer.TryGetRequestModel(body) == E2E.SessionModel)];
      Assert.True(parentBodies.Count >= 6, $"expected 6 parent requests, got {parentBodies.Count}");
      Assert.Contains($"delivered to={childId:D} link=researcher",
          E2E.GetLastToolMessage(parentBodies[5]), StringComparison.Ordinal);

      // ---- Release the park: the child's exec returns, the loop drains the mailbox at the
      // next safe point, and the steering reaches the provider in the child's next request.
      await File.WriteAllTextAsync(marker, "go", TestContext.Current.CancellationToken);
      deadline = DateTimeOffset.UtcNow.AddSeconds(30);
      bool drained = false;
      while (!drained && DateTimeOffset.UtcNow < deadline)
      {
        drained = second.Mock.RequestBodies.Any(body =>
            MockOpenRouterServer.TryGetRequestModel(body) == "mock/sub-model"
            && body.Contains("hello from the root", StringComparison.Ordinal));
        if (!drained)
        {
          await Task.Delay(50, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
      }

      Assert.True(drained, "the child never drained the routed steering");

      // ---- And the re-pointed consent is durable: a store over the same file (a third
      // session's hydration path) sees exactly the final row.
      SqliteLinkStore verify = new(new AppDatabase(dbPath));
      Result<IReadOnlyList<StoredLink>> persisted = verify.List("app");
      Assert.True(persisted.IsSuccess);
      StoredLink row = Assert.Single(persisted.Value);
      Assert.Equal("researcher", row.Name);
      Assert.Equal(childId.ToString("D"), row.AgentAddress);
    }
    finally
    {
      Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", null);
      // Named decision (CA1031): temp cleanup is best effort.
#pragma warning disable CA1031, S108 // Do not catch general exception types
      try
      {
        File.Delete(dbPath);
      }
      catch
      {
      }

      try
      {
        File.Delete(marker);
      }
      catch
      {
      }
#pragma warning restore CA1031, S108
    }
  }

  private sealed class SilentChannel : IClarifyChannel
  {
    public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<string>(new DomainError("Cancelled", "no clarify expected in this E2E scenario")));
  }
}
