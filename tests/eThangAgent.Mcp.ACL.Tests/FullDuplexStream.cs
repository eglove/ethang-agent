using System.Collections.Concurrent;

namespace eThangAgent.Mcp.ACL.Tests;

/// <summary>Two linked in-memory duplex streams (pure managed, no OS handles):
///     bytes written to one end arrive at the other's read side. CreatePair returns
///     the two connected ends. Read blocks until bytes arrive or the peer disposes.</summary>
#pragma warning disable CA1515, CA1844 // test helper
public sealed class FullDuplexStream : Stream
{
  private readonly BlockingCollection<byte> _receive;
  private readonly BlockingCollection<byte> _send;

  private FullDuplexStream(BlockingCollection<byte> receive, BlockingCollection<byte> send)
  {
    _receive = receive;
    _send = send;
  }

  public static (FullDuplexStream Client, FullDuplexStream Server) CreatePair()
  {
    BlockingCollection<byte> toServer = new(boundedCapacity: 1 << 20);
    BlockingCollection<byte> toClient = new(boundedCapacity: 1 << 20);
    FullDuplexStream client = new(toServer, toClient);
    FullDuplexStream server = new(toClient, toServer);
    return (client, server);
  }

  public override bool CanRead => true;
  public override bool CanSeek => false;
  public override bool CanWrite => true;
  public override long Length => throw new NotSupportedException();
  public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

  public override void Flush()
  {
  }

  public override int Read(byte[] buffer, int offset, int count)
  {
    ArgumentNullException.ThrowIfNull(buffer);
    // Short-read semantics: block only for the FIRST byte, then return whatever
    // is already queued. Blocking for the full count deadlocks a transport that
    // asks for a large buffer while the message is smaller.
    byte first;
    try
    {
      // Poll instead of blocking Take: a bounded wait keeps every pump interruptible
      // when the peer closes. IsCompleted exits the loop once CompleteAdding ran.
      while (!_receive.TryTake(out first, TimeSpan.FromMilliseconds(100)))
      {
        if (_receive.IsCompleted)
        {
          // collection closed by the peer
          return 0;
        }
      }
    }
    catch (InvalidOperationException)
    {
      // collection closed by the peer
      return 0;
    }

    buffer[offset] = first;
    int read = 1;
    while (read < count && _receive.TryTake(out byte next))
    {
      buffer[offset + read] = next;
      read++;
    }

    return read;
  }

  public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
      Task.Run(() => Read(buffer, offset, count), cancellationToken);

  public override void Write(byte[] buffer, int offset, int count)
  {
    ArgumentNullException.ThrowIfNull(buffer);
    for (int i = 0; i < count; i++)
    {
      _send.Add(buffer[offset + i]);
    }
  }

  public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
  {
    foreach (byte b in buffer.Span)
    {
      cancellationToken.ThrowIfCancellationRequested();
      _send.Add(b, CancellationToken.None);
    }

    return ValueTask.CompletedTask;
  }

  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  public override void SetLength(long value) => throw new NotSupportedException();

  protected override void Dispose(bool disposing)
  {
    _receive.CompleteAdding();
    _send.CompleteAdding();
    base.Dispose(disposing);
  }
}
#pragma warning restore CA1515, CA1844
