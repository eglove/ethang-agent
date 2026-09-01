using System.Text.Json;

namespace eThangAgent.Transport.ACL;

/// <summary>One wire message between the app and the child host. Kind mirrors a domain
///     operation (start | deliver | interrupt | event | settle | ack); Json carries the
///     domain-shaped payload; Sequence orders at-least-once delivery and pairs acks.</summary>
public sealed record TransportEnvelope(string Kind, string Json, long Sequence)
{
  public static readonly string[] KnownKinds = ["start", "deliver", "interrupt", "event", "settle", "ack"];
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

/// <summary>Declared transport failure (not a hang, not a silent drop — P3/A3).</summary>
public sealed class TransportClosedException : IOException
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

/// <summary>Length-prefixed JSON framing over any Stream (named pipes in production,
///     memory streams in tests). One envelope = one 4-byte little-endian length + UTF-8
///     payload. Oversize frames are rejected at the seam — never buffered blindly.</summary>
public static class TransportFraming
{
  public const int MaxFrameBytes = 4 * 1024 * 1024;

  public static async Task WriteAsync(Stream stream, TransportEnvelope envelope, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(envelope);
    byte[] payload = JsonSerializer.SerializeToUtf8Bytes(envelope);
    if (payload.Length > MaxFrameBytes)
    {
      throw new InvalidOperationException($"frame of {payload.Length} bytes exceeds the {MaxFrameBytes}-byte seam limit.");
    }

    byte[] header = [.. BitConverter.GetBytes(payload.Length)];
    await stream.WriteAsync(header, ct).ConfigureAwait(false);
    await stream.WriteAsync(payload, ct).ConfigureAwait(false);
    await stream.FlushAsync(ct).ConfigureAwait(false);
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
