using System.Text.Json;

namespace eThangAgent.Transport.ACL;

/// <summary>One wire message between the app and the child host. Kind mirrors a domain
///     operation (start | deliver | interrupt | event | settle | ack | root-turn |
///     root-turn-event | session-response | agent-event | root-steer); Json carries the
///     domain-shaped payload; Sequence orders at-least-once delivery and pairs acks.</summary>
public sealed record TransportEnvelope(string Kind, string Json, long Sequence)
{
  /// <summary>Every kind this protocol defines. The root-turn pair rides the same framing
  ///     and the same receive pumps as the child-run kinds — they are wire kinds, not a
  ///     separate channel.</summary>
  public static readonly string[] KnownKinds =
      ["start", "deliver", "interrupt", "event", "settle", "ack", "root-turn", "root-turn-event", "session-response", "agent-event", "root-steer"];

  /// <summary>True when this envelope's kind is one the protocol defines.
  /// <para>HONEST SCOPE: no RECEIVER consults this today — both pumps still fall through
  ///     their switch's <c>default</c> to ignore unknown kinds, which is the same
  ///     forward-compatibility rule this predicate expresses. It exists so the kind list has
  ///     a reader and a test pinning it (a kind added to a pump without the list fails
  ///     <c>KnownKinds_RecognizesEveryKindTheProtocolUses</c>); a receiver that needs to
  ///     DISTINGUISH "unknown" from "known but unhandled" — task 9's host validation is the
  ///     natural place — should use it rather than re-listing the kinds.</para></summary>
  public bool HasKnownKind => Array.IndexOf(KnownKinds, Kind) >= 0;

  /// <summary>Whether a receiver must confirm delivery of this envelope.
  ///     <para>AN ACK IS NEVER ACKED. An ack confirms that a frame arrived, so it carries no
  ///     delivery of its own to confirm; acking one makes each side's ack a new frame for the
  ///     other, and the two pumps ack each other forever. Both pumps ask this before replying,
  ///     so the rule has one home instead of two copies that can drift.</para></summary>
  public bool ShouldAcknowledge => !string.Equals(Kind, "ack", StringComparison.Ordinal);
}

/// <summary>The child-host transport seam (FR-X2). The domain never references this
///     project; the ACL translates both directions. Delivery semantics are DECLARED at the
///     seam (FR-X3): sends are at-least-once; the receiver acks by Sequence; a dropped
///     connection surfaces as a declared failure, never a hang.</summary>
public interface IChildTransport
{
  Task ConnectAsync(CancellationToken ct = default);

  Task SendAsync(TransportEnvelope envelope, CancellationToken ct = default);

  /// <summary>Receives the next envelope; throws a declared TransportClosedException on
  ///     connection loss instead of blocking forever.</summary>
  Task<TransportEnvelope> ReceiveAsync(CancellationToken ct = default);

  ValueTask DisposeAsync();
}

/// <summary>Declared transport failure (not a hang, not a silent drop — P3/A3).
/// <para>Deliberately NOT sealed: <see cref="TransportFrameInterruptedException"/> is a
///     closed connection too, so it derives from this type and every existing
///     <c>catch (TransportClosedException)</c> site keeps treating a desynchronized stream
///     as the connection loss it is.</para></summary>
public class TransportClosedException : IOException
{
  public TransportClosedException()
  {
  }

  public TransportClosedException(string message)
      : base(message)
  {
  }

  public TransportClosedException(string message, Exception innerException)
      : base(message, innerException)
  {
  }
}

/// <summary>The connection is gone because a write was cancelled AFTER its frame header
///     reached the wire (see <see cref="TransportFraming.WriteAsync"/>). This is a
///     DISTINCT condition from an ordinary cancellation: the peer holds a frame header
///     whose payload will never arrive, so it can never parse another frame, and every
///     later frame this side writes is silently consumed as that frame's payload. Nothing
///     on this connection can be recovered — the caller must tear it down.
/// <para>It derives from <see cref="TransportClosedException"/> because the connection IS
///     closed; the subclass exists so the runtime can tell "the peer is gone" from "I
///     desynchronized the stream" and report the honest reason.</para></summary>
public sealed class TransportFrameInterruptedException : TransportClosedException
{
  public TransportFrameInterruptedException()
  {
  }

  public TransportFrameInterruptedException(string message)
      : base(message)
  {
  }

  public TransportFrameInterruptedException(string message, Exception innerException)
      : base(message, innerException)
  {
  }
}

/// <summary>One frame was too large to write, and NOTHING was put on the wire for it: the write
///     is refused before its header, so the stream and the connection are intact and the same
///     transport can carry the next frame.
/// <para>It derives from <see cref="IOException"/> and NOT from
///     <see cref="TransportClosedException"/>, which is the distinction it exists for: a
///     <c>catch (TransportClosedException)</c> site means 'the connection is gone', and this
///     condition is the opposite — the connection survives and only this payload was
///     unsendable. The frame's own size and the limit it exceeded travel on the exception so
///     the answer can name both without re-measuring.</para></summary>
public sealed class TransportFrameTooLargeException : IOException
{
  public TransportFrameTooLargeException()
  {
  }

  public TransportFrameTooLargeException(string message)
      : base(message)
  {
  }

  public TransportFrameTooLargeException(string message, Exception innerException)
      : base(message, innerException)
  {
  }

  /// <summary>The size of the frame that was refused, in bytes, and the limit it exceeded.</summary>
  public TransportFrameTooLargeException(string message, int frameBytes, int limitBytes)
      : base(message)
  {
    FrameBytes = frameBytes;
    LimitBytes = limitBytes;
  }

  /// <summary>The refused frame's serialized size in bytes, or 0 when the exception was built
  ///     without a measurement.</summary>
  public int FrameBytes { get; }

  /// <summary>The framing limit the frame exceeded, in bytes, or 0 when the exception was built
  ///     without a measurement.</summary>
  public int LimitBytes { get; }
}

/// <summary>Length-prefixed JSON framing over any Stream (named pipes in production,
///     memory streams in tests). One envelope = one 4-byte little-endian length + UTF-8
///     payload. Oversize frames are rejected at the seam — never buffered blindly.</summary>
public static class TransportFraming
{
  public const int MaxFrameBytes = 4 * 1024 * 1024;

  /// <summary>The size the given envelope would occupy as one frame, so a caller can decide
  ///     before the write whether the frame is sendable at all.</summary>
  public static int MeasureBytes(TransportEnvelope envelope)
  {
    ArgumentNullException.ThrowIfNull(envelope);
    return JsonSerializer.SerializeToUtf8Bytes(envelope).Length;
  }

  /// <summary>Writes one frame: a 4-byte little-endian length header, then the payload.
  /// <para>FRAME INTEGRITY IS THE WRITE'S CONTRACT. The header and the payload are two
  ///     separate stream writes, so a cancellation landing between them (or inside the
  ///     payload) leaves the peer holding a header whose bytes never arrive. The peer's
  ///     <see cref="ReadExactlyAsync"/> then blocks forever, and every frame written
  ///     afterwards is silently swallowed as that frame's payload — the connection is dead
  ///     while both sides still believe it is alive. Measured: a peer that stalls after
  ///     reading the header never parses another frame, and three later frames written
  ///     successfully on the same stream are never read.</para>
  /// <para>So a cancellation is not simply rethrown here. If it interrupted a frame, the
  ///     stream is UNRECOVERABLE and this method says so with
  ///     <see cref="TransportFrameInterruptedException"/> — a cancellation that lands
  ///     BEFORE the first byte of the frame leaves the stream intact and rethrows the
  ///     plain <see cref="OperationCanceledException"/>.</para></summary>
  public static async Task WriteAsync(Stream stream, TransportEnvelope envelope, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(envelope);
    byte[] payload = JsonSerializer.SerializeToUtf8Bytes(envelope);
    // Refused BEFORE the header is written: nothing has reached the wire, so the stream stays
    // framed from the peer's point of view and this side keeps the connection.
    if (payload.Length > MaxFrameBytes)
    {
      throw new TransportFrameTooLargeException(
          $"frame of {payload.Length} bytes exceeds the {MaxFrameBytes}-byte seam limit.",
          payload.Length, MaxFrameBytes);
    }

    byte[] header = [.. BitConverter.GetBytes(payload.Length)];
    // Nothing is on the wire until the header write begins, so a cancellation here is
    // recoverable and must stay an ordinary cancellation.
    await stream.WriteAsync(header, ct).ConfigureAwait(false);
    try
    {
      await stream.WriteAsync(payload, ct).ConfigureAwait(false);
      await stream.FlushAsync(ct).ConfigureAwait(false);
    }
    catch (OperationCanceledException ex)
    {
      // The header is already on the wire and the payload is not (or only partly): the
      // peer's frame stream is desynchronized from here on.
      throw new TransportFrameInterruptedException(
          "the frame was interrupted after its header reached the wire; the peer's frame stream is desynchronized and the connection cannot be reused.",
          ex);
    }
  }

  public static async Task<TransportEnvelope> ReadAsync(Stream stream, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(stream);
    byte[] header = await ReadExactlyAsync(stream, 4, ct).ConfigureAwait(false);
    int length = BitConverter.ToInt32(header, 0);
    if (length is < 0 or > MaxFrameBytes)
    {
      throw new TransportClosedException($"peer sent an invalid frame length {length}.");
    }

    byte[] payload = await ReadExactlyAsync(stream, length, ct).ConfigureAwait(false);
    return JsonSerializer.Deserialize<TransportEnvelope>(payload)
        ?? throw new TransportClosedException("peer sent a null envelope.");
  }

  private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken ct)
  {
    byte[] buffer = new byte[count];
    int read = 0;
    while (read < count)
    {
      int chunk = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
      if (chunk == 0)
      {
        throw new TransportClosedException("connection closed mid-frame.");
      }

      read += chunk;
    }

    return buffer;
  }
}
