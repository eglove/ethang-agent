using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.AgentInfrastructure;
using eThangAgent.CapabilityDomain;
using eThangAgent.Composition;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>W3.4 remote variant (the BDD "A route crosses into the child host"):
///     session B runs RemoteHost=true so its child executes in the ChildHost process;
///     session A (in-process) routes through a consented link; the app-side locator
///     resolves B's owned remote child and the delivery rides the wire into the host's
///     mailbox; the child DRAINS it at its next safe point and the text reaches the
///     provider (visible in the child's transcript request). Also pinned: the
///     cross-container audit event lands on B's app-side stream (the owning container's
///     stream). Real host exe, real database, mock provider. Every await bounded
///     (deadlock vigilance); both containers are disposed in a guard finally so a
///     failure never orphans a live ChildHost against this test's database.</summary>
[Collection("Desktop E2E")]
public class CrossContainerRemoteRouteE2ETests
{
  private static string RawCompletion(string content) =>
      System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });

  private static AgentSettings Settings(Uri openRouterBaseUrl, bool remoteHost) => new(
      new OpenRouterSettings("sk-or-test", openRouterBaseUrl),
      new ZaiSettings(null, new Uri("https://zai.test")),
      new SubAgentOptions(null, 2),
      RemoteHost: remoteHost);

  private sealed class NeverAsk : IClarifyChannel
  {
    public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<string>(new DomainError("Cancelled", "no clarify in this E2E")));
  }

  private sealed class Capture : IAgentEventSubscriber
  {
    public List<ChildEvent> Events { get; } = [];

    public void OnEvent(ChildEvent evt) => Events.Add(evt);
  }

  [Fact]
  public async Task Route_From_Session_A_Reaches_Remote_Child_Of_Session_B()
  {
    string dbPath = Path.Combine(Path.GetTempPath(), $"ethang-crossremote-{Guid.NewGuid():N}.db");
    string wsA = Directory.CreateTempSubdirectory("ethang-crossremote-a").FullName;
    string wsB = Directory.CreateTempSubdirectory("ethang-crossremote-b").FullName;
    try
    {
      MockOpenRouterServer mock = new();
      mock.Start();
      _ = mock.ReturnsCatalog(CatalogJson());
      try
      {
        ProcessMailboxLocator locator = new();
        // A: in-process. B: remote host — same app process, one locator, shared db.
        AgentSessionFactory factoryA = new(Settings(new Uri("https://openrouter.test"), remoteHost: false),
            new AppDatabase(dbPath), locator);
        AgentSessionFactory factoryB = new(Settings(mock.BaseUrl, remoteHost: true),
            new AppDatabase(dbPath), locator);

        Result<AgentSession> a = await factoryA.CreateAsync(wsA, Providers.OpenRouter,
            new NeverAsk(), ct: TestContext.Current.CancellationToken);
        Result<AgentSession> b = await factoryB.CreateAsync(wsB, Providers.OpenRouter,
            new NeverAsk(), ct: TestContext.Current.CancellationToken);
        Assert.True(a.IsSuccess, a.Error?.Message);
        Assert.True(b.IsSuccess, b.Error?.Message);

        Capture capture = new();
        IDisposable lease = b.Value.Services.GetRequiredService<IAgentEvents>().Subscribe(capture);
        try
        {
          await RunRouteScenarioAsync(a.Value, b.Value, mock, capture, marker =>
          {
            File.WriteAllText(marker, "go");
          }).WaitAsync(TimeSpan.FromSeconds(180), TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
        finally
        {
          lease.Dispose();
          // B owns the ChildHost process: a leaked container would orphan a live host
          // against this test's database — disposal is unconditional (guard finally).
          await a.Value.Services.DisposeAsync().ConfigureAwait(true);
          await b.Value.Services.DisposeAsync().ConfigureAwait(true);
        }
      }
      finally
      {
        mock.Dispose();
      }
    }
    finally
    {
      Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", null);
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
        Directory.Delete(wsA, true);
      }
      catch
      {
      }

      try
      {
        Directory.Delete(wsB, true);
      }
      catch
      {
      }
#pragma warning restore CA1031, S108
    }
  }

  /// <summary>The park/route/drain scenario over two opened sessions, extracted so the
  ///     test body's guard finally can own disposal unconditionally. Bounded waits only.
  ///     The release action is injected because the marker file is created here.</summary>
  private static async Task RunRouteScenarioAsync(AgentSession a, AgentSession b,
      MockOpenRouterServer mock, Capture capture, Action<string> releasePark)
  {
    // B's parent spawns the remote child through the production surface; the child
    // PARKS on a marker file mid-tool so the route lands while it is running.
    string marker = Path.Combine(Path.GetTempPath(), "ethang-crossremote-marker-" + Guid.NewGuid().ToString("N"));
    File.Delete(marker);
    try
    {
      b.Preferences?.ModelId = "openrouter/auto";

      string spawnProgram = "return Tools.Invoke(\"agent.spawn\", new { timeoutSeconds = 60, taskPrompt = \"Remote child echoes steering.\", model = \"mock/sub-model\", label = \"cross-remote\" });";
      _ = mock.ReturnsForModel("openrouter/auto",
          ExecToolCall("b_call_1", ExecProgram(spawnProgram)),
          RawCompletion("spawning"));
      string parkedExec = "var deadline = System.DateTime.UtcNow.AddSeconds(60);" +
          "while (!System.IO.File.Exists(@\"" + marker + "\")) {" +
          " if (System.DateTime.UtcNow > deadline) return \"park-timeout\";" +
          " await System.Threading.Tasks.Task.Delay(50); } return \"released\";";
      _ = mock.ReturnsForModel("mock/sub-model",
          ExecToolCall("child_call_1", ExecProgram(parkedExec)),
          RawCompletion("remote child done"));

      SendMessageCommandHandler handlerB = b.Handler;
      Result<string> turn = await handlerB.Handle(new SendMessageCommand("spawn the remote helper"),
          ct: TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(60),
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(turn.IsSuccess, turn.Error?.Message);

      // Bounded wait for the remote child to reach its parked exec call (its provider
      // requests carry the child's model id).
      DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(45);
      while (!mock.RequestBodies.Any(body => MockOpenRouterServer.TryGetRequestModel(body) == "mock/sub-model")
          && DateTimeOffset.UtcNow < deadline)
      {
        await Task.Delay(100, TestContext.Current.CancellationToken).ConfigureAwait(true);
      }

      Assert.True(mock.RequestBodies.Any(body => MockOpenRouterServer.TryGetRequestModel(body) == "mock/sub-model"),
          "the remote child never reached its first provider call");

      // The child's id comes from the shared store (exact, no receipt parsing): the
      // running depth-1 row labeled cross-remote.
      SqliteAgentStore store = new(new AppDatabase(DbPathOf(b)));
      Guid bChild = await WaitForRunningChildAsync(store).WaitAsync(TimeSpan.FromSeconds(20),
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.NotEqual(Guid.Empty, bChild);

      // Consent: A links to B's remote child.
      AgentLinkRegistry aLinks = a.Services.GetRequiredService<AgentLinkRegistry>();
      Result<LinkAddress> consented = aLinks.Link("remote-peer", "container-b", bChild.ToString("D"), consented: true);
      Assert.True(consented.IsSuccess);

      // A routes; the locator resolves the proxy source (B owns the child remotely)
      // and the delivery rides the wire into the host's mailbox.
      AgentCapabilityProvider providerA = a.Services.GetRequiredService<AgentCapabilityProvider>();
      CapabilityInvocationResult routed = await providerA.InvokeAsync("route",
          $"{{\"name\":\"remote-peer\",\"text\":\"hello across processes\",\"urgency\":\"attention\"}}",
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.False(routed.IsError, routed.Content);
      Assert.Equal($"delivered to={bChild:D} link=remote-peer", routed.Content);

      // Target-side audit on B's app-side stream (the owning container's stream).
      MessageDeliveredEvent audited = Assert.Single(capture.Events.OfType<MessageDeliveredEvent>(),
          e => "cross-container".Equals(e.Direction, StringComparison.Ordinal));
      Assert.Equal(bChild, audited.ChildId.Value);

      // Release the park: the child drains its host-side mailbox at the next safe
      // point and the routed text reaches the provider (the child's transcript).
      releasePark(marker);
      deadline = DateTimeOffset.UtcNow.AddSeconds(45);
      bool drained = false;
      while (!drained && DateTimeOffset.UtcNow < deadline)
      {
        drained = mock.RequestBodies.Any(body =>
            MockOpenRouterServer.TryGetRequestModel(body) == "mock/sub-model"
            && body.Contains("hello across processes", StringComparison.Ordinal));
        if (!drained)
        {
          await Task.Delay(100, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
      }

      Assert.True(drained, "the remote child never drained the routed steering");
    }
    finally
    {
#pragma warning disable CA1031, S108 // Do not catch general exception types
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

  /// <summary>Polls (bounded by the caller's WaitAsync) for the running depth-1 child
  ///     row the remote spawn persisted into the shared store.</summary>
  private static async Task<Guid> WaitForRunningChildAsync(SqliteAgentStore store)
  {
    while (true)
    {
      Result<IReadOnlyList<AgentRecord>> listed = await store.ListAllAsync(
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      if (listed.IsSuccess)
      {
        AgentRecord? running = listed.Value.FirstOrDefault(r =>
            r.Depth == 1 && r.Status == AgentStatus.Running && r.Label == "cross-remote");
        if (running is { })
        {
          return running.Id.Value;
        }
      }

      await Task.Delay(100, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }
  }

  /// <summary>The database path behind a session — resolved from its own store wiring
  ///     via the environment the harness sets (the factory was built over dbPath).</summary>
  private static string DbPathOf(AgentSession session) => session.Services.GetRequiredService<AppDatabase>().DatabasePath;

  private static string CatalogJson() =>
      /*lang=json,strict*/ "{ \"data\": [ { \"id\": \"mock/sub-model\", \"pricing\": { \"prompt\": \"0.000001\", \"completion\": \"0.000002\" }, \"context_length\": 32768, \"top_provider\": { \"max_completion_tokens\": 8192 }, \"architecture\": { \"modality\": \"text->text\" } } ] }";

  private static string ExecProgram(string program) =>
      System.Text.Json.JsonSerializer.Serialize(new { timeoutSeconds = 120, program });

  private static string ExecToolCall(string id, string arguments) =>
      System.Text.Json.JsonSerializer.Serialize(new
      {
        choices = new[]
          {
                new
                {
                    message = new
                    {
                        content = (string?)null,
                        tool_calls = new[]
                        {
                            new { id, type = "function", function = new { name = "exec", arguments } }
                        }
                    }
                }
          }
      });
}
