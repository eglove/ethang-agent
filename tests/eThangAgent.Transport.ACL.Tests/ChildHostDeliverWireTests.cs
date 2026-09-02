using eThangAgent.AgentDomain;
using eThangAgent.ChildHost;
using eThangAgent.SharedKernel;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>W5.1 direct wire test for ChildHostServer's deliver path (found broken by
///     the W3.4 remote E2E): a 'deliver' envelope for a RUNNING child reaches the mailbox
///     the child loop actually drains (the host container's registry — the same box the
///     runtime registered at BeginRun), with urgency and sender preserved; a deliver for
///     an unknown or settled child id is dropped silently with no fault, and the serve
///     loop stays alive. The rig attaches delivery targets the way RunChildAsync does
///     (per-child registry view), without spawning a real child.</summary>
public class ChildHostDeliverWireTests
{
  private sealed class RecordingMailbox : IAgentMailbox
  {
    public List<PendingMessage> Received { get; } = [];

    public Result<bool> Deliver(PendingMessage message)
    {
      Received.Add(message);
      return Result.Success(true);
    }

    public IReadOnlyList<PendingMessage> Drain() => Received;

    public int UnreadCount => Received.Count;
  }

  [Fact]
  public async Task Deliver_ForRunningChild_ReachesTheRegisteredMailbox_PreservingMetadata()
  {
    (ChildHostServer server, NamedPipeChildTransport app, Func<Task> _) = await StartRigAsync().ConfigureAwait(true);
    NamedPipeChildTransport appSide = app;
    try
    {
      Guid child = Guid.NewGuid();
      RecordingMailbox box = new();
      ChildMailboxRegistry registry = new();
      registry.Register(new AgentId(child), box);
      server.AttachDeliveryRegistry(child, registry);

      await appSide.SendAsync(DeliverEnvelope(child, "steer now", 2, "parent:root"),
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      await WaitUntil(() => box.Received.Count > 0, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

      PendingMessage message = Assert.Single(box.Received);
      Assert.Equal("steer now", message.Text);
      Assert.Equal(MessageUrgency.Urgent, message.Urgency);
      Assert.Equal("parent:root", message.Sender);

      // The loop is still serving: an interrupt is acked without fault (liveness).
      await appSide.SendAsync(new TransportEnvelope("interrupt",
          System.Text.Json.JsonSerializer.Serialize(new InterruptCommand(child)), 42),
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      TransportEnvelope ack = await ReceiveUntilKindAsync(appSide, "ack", 42).WaitAsync(
          TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.Equal(42, ack.Sequence);
    }
    finally
    {
      await appSide.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task Deliver_ForUnknownChild_IsDroppedSilently_LoopKeepsServing()
  {
    (ChildHostServer _, NamedPipeChildTransport appSide, Func<Task> _) = await StartRigAsync().ConfigureAwait(true);
    try
    {
      Guid nobody = Guid.NewGuid();
      TransportEnvelope deliverEnvelope = DeliverEnvelope(nobody, "ghost", 0, "parent");
      TransportEnvelope interruptEnvelope = new("interrupt",
          System.Text.Json.JsonSerializer.Serialize(new InterruptCommand(nobody)),
          deliverEnvelope.Sequence + 1);
      await appSide.SendAsync(deliverEnvelope, TestContext.Current.CancellationToken).ConfigureAwait(true);

      // The server did not fault: the interrupt after the dropped deliver is still
      // processed and acked. The opening 'declare' plus both acks arrive in order.
      await appSide.SendAsync(interruptEnvelope, TestContext.Current.CancellationToken).ConfigureAwait(true);
      TransportEnvelope interruptAck2 = await ReceiveUntilKindAsync(appSide, "ack",
          interruptEnvelope.Sequence).WaitAsync(TimeSpan.FromSeconds(15),
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.Equal(interruptEnvelope.Sequence, interruptAck2.Sequence);
    }
    finally
    {
      await appSide.DisposeAsync().ConfigureAwait(true);
    }
  }

  private static TransportEnvelope DeliverEnvelope(Guid id, string text, int urgency, string sender)
      => new("deliver", System.Text.Json.JsonSerializer.Serialize(new DeliverCommand(id, text, urgency, sender)), NextSeq());

  private static long _seq;

  private static long NextSeq() => Interlocked.Increment(ref _seq);

  /// <summary>Reads frames until one of the wanted kind (and sequence, when > 0) arrives;
  ///     interleaved traffic (the opening declare, acks of other envelopes) is skipped.
  ///     Bounded by the caller's WaitAsync.</summary>
  private static async Task<TransportEnvelope> ReceiveUntilKindAsync(
      NamedPipeChildTransport transport, string kind, long sequence)
  {
    while (true)
    {
      TransportEnvelope frame = await transport.ReceiveAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
      if (frame.Kind == kind && (sequence <= 0 || frame.Sequence == sequence))
      {
        return frame;
      }
    }
  }

  private static async Task WaitUntil(Func<bool> condition, TimeSpan limit)
  {
    DateTimeOffset deadline = DateTimeOffset.UtcNow + limit;
    while (!condition())
    {
      if (DateTimeOffset.UtcNow > deadline)
      {
        Assert.Fail("condition not met within the limit");
      }

      await Task.Delay(50, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }
  }

  private static async Task<(ChildHostServer Server, NamedPipeChildTransport App, Func<Task> Serve)> StartRigAsync()
  {
    string settings = Path.Combine(Path.GetTempPath(), "ethang-deliverrig-" + Guid.NewGuid().ToString("N") + ".json");
    await File.WriteAllTextAsync(settings, /*lang=json,strict*/ "{\"OpenRouter\":{\"ApiKey\":null},\"Zai\":{\"ApiKey\":null}}",
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    ChildHostServer server = new(settings, Path.Combine(Path.GetTempPath(), "ethang-deliverrig.db"));
    string pipeName = "ethang-deliverrig-" + Guid.NewGuid().ToString("N");
    Task<NamedPipeChildTransport> accept = NamedPipeChildTransport.AcceptAppAsync(pipeName, TestContext.Current.CancellationToken);
    NamedPipeChildTransport app = await NamedPipeChildTransport.ConnectToHostAsync(pipeName,
        TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    NamedPipeChildTransport hostSide = await accept.WaitAsync(TimeSpan.FromSeconds(10),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    server.AttachTransport(hostSide);
    Task serve = server.ServeAsync();
    return (server, app, () => serve);
  }
}
