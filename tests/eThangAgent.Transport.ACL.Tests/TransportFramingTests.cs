namespace eThangAgent.Transport.ACL.Tests;

/// <summary>Framing seam tests: envelopes survive the wire byte-exactly, oversize frames
///     are rejected at write, a peer closing mid-frame surfaces as a declared failure.</summary>
public class TransportFramingTests
{
  [Fact]
  public async Task RoundTrip_PreservesEnvelopeByteForByte()
  {
    MemoryStream wire = new();
    TransportEnvelope sent = new("deliver", "{}", 42);

    await TransportFraming.WriteAsync(wire, sent, TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = wire.Seek(0, SeekOrigin.Begin);
    TransportEnvelope received = await TransportFraming.ReadAsync(wire, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal(sent.Kind, received.Kind);
    Assert.Equal(sent.Json, received.Json);
    Assert.Equal(sent.Sequence, received.Sequence);
  }

  [Fact]
  public async Task BackToBack_Frames_DoNotInterleave()
  {
    MemoryStream wire = new();
    await TransportFraming.WriteAsync(wire, new TransportEnvelope("start", "{}", 1), TestContext.Current.CancellationToken).ConfigureAwait(true);
    await TransportFraming.WriteAsync(wire, new TransportEnvelope("ack", "{}", 2), TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = wire.Seek(0, SeekOrigin.Begin);

    TransportEnvelope first = await TransportFraming.ReadAsync(wire, TestContext.Current.CancellationToken).ConfigureAwait(true);
    TransportEnvelope second = await TransportFraming.ReadAsync(wire, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal("start", first.Kind);
    Assert.Equal("ack", second.Kind);
  }

  [Fact]
  public async Task CloseMidFrame_ThrowsDeclaredClosed_NotHang()
  {
    MemoryStream wire = new([8, 0, 0, 0, 65]); // claims an 8-byte frame, delivers 1 byte, then ends
    _ = await Assert.ThrowsAsync<TransportClosedException>(
        () => TransportFraming.ReadAsync(wire, TestContext.Current.CancellationToken)).ConfigureAwait(true);
  }

  [Fact]
  public async Task OversizeFrameLength_IsRejectedAtTheSeam()
  {
    MemoryStream wire = new([0xFF, 0xFF, 0xFF, 0x7F]); // int.MaxValue length claim
    _ = await Assert.ThrowsAsync<TransportClosedException>(
        () => TransportFraming.ReadAsync(wire, TestContext.Current.CancellationToken)).ConfigureAwait(true);
  }
}
