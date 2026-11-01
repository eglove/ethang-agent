using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.ChildHost;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>Issue #330: a cell's chat input must reach an agent that runs in its OWN host.
///     <para>THE DEFECT THIS CLOSES: steering was addressed to a ROOT TURN, and an agent started
///     by a chain step runs as a child run - it never populates the host's root-turn registry, so
///     a steer addressed at it found no live turn and was dropped by the path's own pinned
///     contract. The app could not even obtain a turn id to address: a forwarded child event
///     carries a phase and a short tag, never one. A host serves exactly ONE agent, so the agent
///     id IS the address and no turn identity has to cross the wire.</para>
///     <para>EVERY TEST HERE DRIVES THE PRODUCTION PATH: the REAL server, a REAL named pipe, the
///     real dispatch switch, and the real mailbox the agent loop drains. Only the agent's own
///     loop is absent, because what is under test is that the message arrives in the box that
///     loop reads - not that the model answers.</para>
///     <para>THE ANSWER IS THE OTHER HALF: the human typed the message, so a steer that cannot be
///     delivered must come back as a typed failure. A silent drop is indistinguishable from a
///     slow agent, which is the whole reason this path exists.</para></summary>
public class ChildHostAgentSteerWireTests
{
  private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

  /// <summary>A steer addressed by AGENT ID lands in that agent's own mailbox, under the
  ///     identity the app pinned its echo to, and the host answers that it was accepted.
  ///     <para>THE IDENTITY IS THE ASSERTION THAT MAKES THE ECHO WORK: the app pins its echo
  ///     under the id it minted, and the agent loop's drain fact names the ids it consumed -
  ///     so a host that re-minted would leave the echo pinned forever (the #92 handshake).
  ///     </para></summary>
  [Fact]
  public async Task ASteer_AddressedByAgentId_LandsInThatAgentsMailbox_AndIsAcknowledged()
  {
    BoundedAgentMailbox mailbox = new();
    using ScriptedRootHost host = ScriptedRootHost.Start("answer", ["a"]);
    AgentId agent = AgentId.NewId();
    host.Server.AttachAgentSteering(agent.Value, _ => mailbox, host.Services);

#pragma warning disable CA2007 // Named decision: 'await using' cannot carry ConfigureAwait.
    await using NamedPipeChildTransport app = await ConnectToServerAsync(host.Server).ConfigureAwait(true);
#pragma warning restore CA2007

    Guid pinned = Guid.NewGuid();
    await app.SendAsync(new TransportEnvelope("agent-steer",
        JsonSerializer.Serialize(new AgentSteerCommand(agent.Value, "steer left, not up", "human", pinned)),
        1), TestContext.Current.CancellationToken).ConfigureAwait(true);

    AgentSteerAck ack = await ReadAckAsync(app, agent).ConfigureAwait(true);
    Assert.Null(ack.ErrorCode);

    Assert.True(mailbox.TryTake(out string text, out Guid drained));
    Assert.Equal("steer left, not up", text);
    Assert.Equal(pinned, drained);
  }

  /// <summary>A steer to an agent this host is not running is ANSWERED with a typed refusal,
  ///     never dropped: the human must learn that the message went nowhere.
  ///     <para>This is the case the old turn-addressed path could not report at all - it dropped
  ///     the message silently, which reads exactly like a slow agent.</para></summary>
  [Fact]
  public async Task ASteer_ToAnAgentWithNoLiveRun_IsAnsweredWithATypedRefusal()
  {
    using ScriptedRootHost host = ScriptedRootHost.Start("answer", ["a"]);
    AgentId settled = AgentId.NewId();

#pragma warning disable CA2007 // Named decision: 'await using' cannot carry ConfigureAwait.
    await using NamedPipeChildTransport app = await ConnectToServerAsync(host.Server).ConfigureAwait(true);
#pragma warning restore CA2007

    await app.SendAsync(new TransportEnvelope("agent-steer",
        JsonSerializer.Serialize(new AgentSteerCommand(settled.Value, "anyone there?", "human")), 2),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    AgentSteerAck ack = await ReadAckAsync(app, settled).ConfigureAwait(true);
    Assert.Equal(MailboxErrors.NotRunning, ack.ErrorCode);
  }

  /// <summary>A mailbox that REFUSES the deliver answers with the mailbox's own code, so the
  ///     human reads why - a full box and a closed box are different facts, and both are
  ///     refusals rather than deliveries.</summary>
  [Fact]
  public async Task ASteer_WhoseMailboxRefuses_IsAnsweredWithTheMailboxsOwnCode()
  {
    BoundedAgentMailbox mailbox = new(capacity: 1);
    using ScriptedRootHost host = ScriptedRootHost.Start("answer", ["a"]);
    AgentId agent = AgentId.NewId();
    host.Server.AttachAgentSteering(agent.Value, _ => mailbox, host.Services);
    // Fill the box so the next deliver is refused by the mailbox itself.
    Assert.True(mailbox.Deliver(PendingMessage.New("first", "human")).IsSuccess);

#pragma warning disable CA2007 // Named decision: 'await using' cannot carry ConfigureAwait.
    await using NamedPipeChildTransport app = await ConnectToServerAsync(host.Server).ConfigureAwait(true);
#pragma warning restore CA2007

    await app.SendAsync(new TransportEnvelope("agent-steer",
        JsonSerializer.Serialize(new AgentSteerCommand(agent.Value, "one too many", "human")), 3),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    AgentSteerAck ack = await ReadAckAsync(app, agent).ConfigureAwait(true);
    Assert.Equal(MailboxErrors.Full, ack.ErrorCode);
  }

  /// <summary>ACCEPTANCE IS WHAT RAISES THE CELL'S UNREAD BADGE, and it is published under the
  ///     AGENT's id - the value that cell's badge subscribes with - so the count lands on the
  ///     card that owns the message rather than on the tab.
  ///     <para>Published on acceptance, never on send: a refused post raises nothing, the same
  ///     rule the root steer path follows.</para></summary>
  [Fact]
  public async Task AnAcceptedSteer_PublishesItsAcceptanceFact_UnderTheAgentsOwnId()
  {
    BoundedAgentMailbox mailbox = new();
    using ScriptedRootHost host = ScriptedRootHost.Start("answer", ["a"]);
    AgentId agent = AgentId.NewId();
    host.Server.AttachAgentSteering(agent.Value, _ => mailbox, host.Services);
    IAgentEvents events = (IAgentEvents)host.Services.GetService(typeof(IAgentEvents))!;
    List<ChildEvent> seen = [];
    using IDisposable lease = events.Subscribe(new Collector(seen));

#pragma warning disable CA2007 // Named decision: 'await using' cannot carry ConfigureAwait.
    await using NamedPipeChildTransport app = await ConnectToServerAsync(host.Server).ConfigureAwait(true);
#pragma warning restore CA2007

    Guid pinned = Guid.NewGuid();
    await app.SendAsync(new TransportEnvelope("agent-steer",
        JsonSerializer.Serialize(new AgentSteerCommand(agent.Value, "look at the test output", "human", pinned)), 4),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = await ReadAckAsync(app, agent).ConfigureAwait(true);

    MessageDeliveredEvent delivered = Assert.Single(seen.OfType<MessageDeliveredEvent>());
    Assert.Equal(agent, delivered.ChildId);
    Assert.Equal(pinned, delivered.MessageId);
  }

  /// <summary>NO PATH ADDRESSES A PARENT, A CHILD, OR ANOTHER AGENT (issue #330's explicit
  ///     acceptance item). The steer carries exactly one recipient - the agent the user typed
  ///     into - and the wire's command shape is what enforces it: there is no parent field, no
  ///     child field, and no turn id to misdirect.
  ///     <para>Asserted against the SERIALIZED shape rather than against intent, because a field
  ///     added later is exactly how a second addressee would appear.</para></summary>
  [Fact]
  public void TheSteerCommand_CarriesOneRecipient_AndNoParentOrTurnAddress()
  {
    string json = JsonSerializer.Serialize(new AgentSteerCommand(Guid.NewGuid(), "text", "human", Guid.NewGuid()));
    using JsonDocument document = JsonDocument.Parse(json);
    string[] names = [.. document.RootElement.EnumerateObject().Select(property => property.Name)];

    Assert.Equal(["AgentId", "Text", "Sender", "MessageId"], names);
  }

  /// <summary>Reads frames until the ack for <paramref name="agent"/> arrives, ignoring acks
  ///     for other agents and the host's declare frames.</summary>
  private static async Task<AgentSteerAck> ReadAckAsync(NamedPipeChildTransport app, AgentId agent)
  {
    DateTimeOffset deadline = DateTimeOffset.UtcNow + Limit;
    while (DateTimeOffset.UtcNow < deadline)
    {
      TransportEnvelope envelope = await app.ReceiveAsync()
          .WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
      if (envelope.Kind != "agent-steer-ack")
      {
        continue;
      }

      AgentSteerAck? ack = JsonSerializer.Deserialize<AgentSteerAck>(envelope.Json);
      if (ack?.AgentId == agent.Value)
      {
        return ack;
      }
    }

    Assert.Fail("no agent-steer ack arrived for " + agent + " within the limit");
    throw new InvalidOperationException("unreachable");
  }

  /// <summary>Connects the app side to the ALREADY-BUILT server, so the test drives the SAME
  ///     server instance the rig attached its seams to.</summary>
  private static async Task<NamedPipeChildTransport> ConnectToServerAsync(ChildHostServer server)
  {
    string pipeName = "ethang-agentsteer-" + Guid.NewGuid().ToString("N");
    Task<NamedPipeChildTransport> accept = NamedPipeChildTransport.AcceptAppAsync(pipeName,
        TestContext.Current.CancellationToken);
    NamedPipeChildTransport app = await NamedPipeChildTransport.ConnectToHostAsync(pipeName,
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    NamedPipeChildTransport hostSide = await accept.WaitAsync(Limit, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    server.AttachTransport(hostSide);
    _ = server.ServeAsync();
    return app;
  }

  /// <summary>Collects every fact the container publishes, so a test can assert what the steer
  ///     path raised.</summary>
  private sealed class Collector(List<ChildEvent> seen) : IAgentEventSubscriber
  {
    public void OnEvent(ChildEvent evt) => seen.Add(evt);
  }
}
