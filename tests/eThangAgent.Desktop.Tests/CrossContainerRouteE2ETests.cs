using eThangAgent.AgentDomain;
using eThangAgent.AgentInfrastructure;
using eThangAgent.CapabilityDomain;
using eThangAgent.Composition;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>W3.4 cross-container delivery E2E (in-process variant, the BDD "A route
///     crosses sessions within one process"): TWO factory-built sessions share one app
///     database and one process-wide locator; a live mailbox is registered under a child
///     id of session B (what the runtime does at BeginRun); the link registry of session
///     A holds the child's address; session A's root agent routes through agent.route and
///     the receipt reads exactly as an in-session one; the message lands in B's child's
///     mailbox with urgency and sender intact. Also pinned: the target-side
///     MessageDelivered(cross-container) audit event on B's stream, and the
///     second-app-instance honesty (a second app builds its OWN locator, so a foreign id
///     still fails NotRunning — declared out of scope, the failure is honest, never
///     silent). Drain-at-safe-point through the child's own loop is pinned end-to-end by
///     InProcessSteeringBridgeTests; drain here asserts the box's content directly.
///     Every await bounded (deadlock vigilance).</summary>
[Collection("Desktop E2E")]
public class CrossContainerRouteE2ETests
{
  private static AgentSettings Settings(Uri openRouterBaseUrl) => new(
      new OpenRouterSettings("sk-or-test", openRouterBaseUrl),
      new ZaiSettings(null, new Uri("https://zai.test")),
      new SubAgentOptions(null, 2));

  private sealed class NeverAsk : IClarifyChannel
  {
    public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<string>(new DomainError("Cancelled", "no clarify in this E2E")));
  }

  /// <summary>Subscribes a capture list to a session's event stream; the disposable
  ///     lease is tracked so the test can detach.</summary>
  private sealed class Capture : IAgentEventSubscriber
  {
    public List<ChildEvent> Events { get; } = [];

    public void OnEvent(ChildEvent evt) => Events.Add(evt);
  }

  [Fact]
  public async Task Route_From_Session_A_Crosses_Into_Session_B_Child()
  {
    string dbPath = Path.Combine(Path.GetTempPath(), $"ethang-crossroute-{Guid.NewGuid():N}.db");
    string ws = Directory.CreateTempSubdirectory("ethang-crossroute-ws").FullName;
    try
    {
      ProcessMailboxLocator locator = new();
      AgentSessionFactory factory = new(Settings(new Uri("https://openrouter.test")),
          new AppDatabase(dbPath), locator);

      Result<AgentSession> a = await factory.CreateAsync(ws, Providers.OpenRouter,
          new NeverAsk(), ct: TestContext.Current.CancellationToken);
      Result<AgentSession> b = await factory.CreateAsync(ws, Providers.OpenRouter,
          new NeverAsk(), ct: TestContext.Current.CancellationToken);
      Assert.True(a.IsSuccess, a.Error?.Message);
      Assert.True(b.IsSuccess, b.Error?.Message);

      // A probe on B's event stream — the target-side audit surface (W3.3).
      Capture capture = new();
      IDisposable lease = b.Value.Services.GetRequiredService<IAgentEvents>().Subscribe(capture);

      // A live mailbox under a child id of B — exactly what the runtime registers at
      // BeginRun (the steering-bridge shape).
      Guid bChild = Guid.NewGuid();
      BoundedAgentMailbox bMailbox = new();
      b.Value.Services.GetRequiredService<ChildMailboxRegistry>().Register(new AgentId(bChild), bMailbox);

      // Consent: the user links A to B's child through A's Links-dialog path.
      AgentLinkRegistry aLinks = a.Value.Services.GetRequiredService<AgentLinkRegistry>();
      Result<LinkAddress> consented = aLinks.Link("researcher", "container-b", bChild.ToString("D"), consented: true);
      Assert.True(consented.IsSuccess);

      // A's root routes through the REAL capability surface. A's runtime fails
      // NotRunning (it holds no mailbox for B's child); the locator delivers.
      AgentCapabilityProvider provider = a.Value.Services.GetRequiredService<AgentCapabilityProvider>();
      CapabilityInvocationResult routed = await provider.InvokeAsync("route",
          /*lang=json,strict*/ $"{{\"name\":\"researcher\",\"text\":\"hello from A\",\"urgency\":\"attention\"}}",
          TestContext.Current.CancellationToken);
      Assert.False(routed.IsError, routed.Content);
      Assert.Equal($"delivered to={bChild:D} link=researcher", routed.Content);

      // The message is IN B's child's mailbox.
      Assert.Equal(1, bMailbox.UnreadCount);

      // Target-side audit: exactly one cross-container delivery event, on B's stream.
      MessageDeliveredEvent audited = Assert.Single(capture.Events.OfType<MessageDeliveredEvent>());
      Assert.Equal(bChild, audited.ChildId.Value);
      Assert.Equal("cross-container", audited.Direction);

      // The envelope arrived intact: text, urgency, sender label.
      IReadOnlyList<PendingMessage> drained = bMailbox.Drain();
      PendingMessage message = Assert.Single(drained);
      Assert.Equal("hello from A", message.Text);
      Assert.Equal(MessageUrgency.Attention, message.Urgency);
      Assert.Equal("parent", message.Sender); // container scope => unlabeled root sender label
      lease.Dispose();

      // Second-app-instance honesty: a second app builds its OWN locator; B's child is
      // invisible to it and the route fails NotRunning (BDD: "Another app instance is
      // out of reach"). The link still resolves — persistence (W2) is intact — only the
      // delivery is honestly out of reach.
      AgentSessionFactory secondApp = new(Settings(new Uri("https://openrouter.test")), new AppDatabase(dbPath));
      Result<AgentSession> a2 = await secondApp.CreateAsync(ws, Providers.OpenRouter,
          new NeverAsk(), ct: TestContext.Current.CancellationToken);
      Assert.True(a2.IsSuccess);
      AgentCapabilityProvider p2 = a2.Value.Services.GetRequiredService<AgentCapabilityProvider>();
      CapabilityInvocationResult foreign = await p2.InvokeAsync("route",
          /*lang=json,strict*/ $"{{\"name\":\"researcher\",\"text\":\"anyone there?\"}}",
          TestContext.Current.CancellationToken);
      Assert.True(foreign.IsError);
      Assert.StartsWith("Error [NotRunning]", foreign.Content, StringComparison.Ordinal);

      await a.Value.Services.DisposeAsync().ConfigureAwait(true);
      await b.Value.Services.DisposeAsync().ConfigureAwait(true);
      await a2.Value.Services.DisposeAsync().ConfigureAwait(true);
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
        Directory.Delete(ws, true);
      }
      catch
      {
      }
#pragma warning restore CA1031, S108
    }
  }
}
