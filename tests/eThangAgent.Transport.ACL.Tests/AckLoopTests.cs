namespace eThangAgent.Transport.ACL.Tests;

/// <summary>THE ACK LOOP (measured 2026-11-14): both pumps acked EVERY envelope they
///     received, including acks, so each side's ack was acked back forever. The app and the
///     child host each burned ~6 cores and ~50 MB/s of allocation while doing no work, and
///     the app sat at 2.1 GB.
///     <para>An ack confirms delivery of a frame; an ack is itself a frame with nothing to
///     confirm, so a receiver must never ack one. These tests pin that decision at the seam
///     the pumps share, so neither can drift away from it.</para></summary>
public class AckLoopTests
{
  /// <summary>Every kind the protocol routes draws an ack, EXCEPT an ack.</summary>
  [Fact]
  public void Ack_IsNeverItselfAcknowledged() =>
      Assert.False(new TransportEnvelope("ack", "1", 1).ShouldAcknowledge);

  /// <summary>The suppression is exactly one kind wide: every other routed kind still
  ///     confirms delivery, so the at-least-once contract is unchanged.</summary>
  [Fact]
  public void EveryOtherRoutedKind_IsStillAcknowledged()
  {
    string[] acknowledged =
    [
      "start", "deliver", "interrupt", "event", "settle", "root-turn",
      "root-turn-event", "session-response", "agent-event", "root-steer",
    ];

    Assert.All(acknowledged, kind => Assert.True(new TransportEnvelope(kind, "{}", 1).ShouldAcknowledge));
  }

  /// <summary>An unknown kind from a newer peer is still acked: the forward-compatibility
  ///     rule is 'ignore what you cannot route', not 'stop confirming delivery'.</summary>
  [Fact]
  public void UnknownKind_FromANewerPeer_IsStillAcknowledged() =>
      Assert.True(new TransportEnvelope("from-a-newer-peer", "{}", 1).ShouldAcknowledge);
}
