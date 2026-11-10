using System.Text;

namespace eThangAgent.Lsp.ACL;

/// <summary>Reads the regular files out of a tar archive held in a seekable stream. Entries
///     are read one at a time, so an archive larger than memory is never expanded in it.</summary>
internal static class LspTarReader
{
  private const int BlockSize = 512;

  /// <summary>Yields each regular file's entry name and an open stream over its bytes. The
  ///     stream is valid until the caller disposes it; the archive is then advanced past the
  ///     entry's padding.</summary>
  public static IEnumerable<(string Name, Stream Content)> Read(Stream archive)
  {
    ArgumentNullException.ThrowIfNull(archive);
    return ReadCore(archive);
  }

  private static IEnumerable<(string Name, Stream Content)> ReadCore(Stream archive)
  {
    byte[] header = new byte[BlockSize];
    while (true)
    {
      if (!ReadExactly(archive, header))
      {
        yield break;
      }

      if (IsZeroBlock(header))
      {
        yield break;
      }

      long size = ReadSize(header);
      char type = (char)header[156];
      string name = ReadName(header);
      if (type is '0' or '\0' or '7')
      {
        LspTarEntryStream content = new(archive, size);
        yield return (name, content);
        content.Drain();
        content.Dispose();
      }

      Skip(archive, Padded(size) - size);
    }
  }

  private static long Padded(long size) => (size + BlockSize - 1) / BlockSize * BlockSize;

  private static bool ReadExactly(Stream stream, byte[] buffer)
  {
    int total = 0;
    while (total < buffer.Length)
    {
      int read = stream.Read(buffer, total, buffer.Length - total);
      if (read == 0)
      {
        return false;
      }

      total += read;
    }

    return true;
  }

  private static bool IsZeroBlock(byte[] block)
  {
    foreach (byte value in block)
    {
      if (value != 0)
      {
        return false;
      }
    }

    return true;
  }

  private static string ReadName(byte[] header)
  {
    string name = ReadField(header, 0, 100);
    string prefix = ReadField(header, 345, 155);
    return prefix.Length == 0 ? name : prefix + "/" + name;
  }

  private static string ReadField(byte[] header, int offset, int length)
  {
    int end = offset;
    while (end < offset + length && header[end] != 0)
    {
      end++;
    }

    return Encoding.UTF8.GetString(header, offset, end - offset);
  }

  private static long ReadSize(byte[] header)
  {
    if ((header[124] & 0x80) != 0)
    {
      long value = header[124] & 0x7F;
      for (int index = 125; index < 136; index++)
      {
        value = (value << 8) | header[index];
      }

      return value;
    }

    string text = ReadField(header, 124, 12).Trim();
    return text.Length == 0 ? 0 : Convert.ToInt64(text, 8);
  }

  private static void Skip(Stream stream, long count)
  {
    if (count <= 0)
    {
      return;
    }

    if (stream.CanSeek)
    {
      stream.Seek(count, SeekOrigin.Current);
      return;
    }

    byte[] scratch = new byte[Math.Min(count, 8192)];
    long remaining = count;
    while (remaining > 0)
    {
      int read = stream.Read(scratch, 0, (int)Math.Min(scratch.Length, remaining));
      if (read == 0)
      {
        return;
      }

      remaining -= read;
    }
  }
}

/// <summary>A read-only window over one tar entry's bytes inside the archive.</summary>
internal sealed class LspTarEntryStream : Stream
{
  private readonly Stream _inner;
  private readonly long _length;
  private long _remaining;

  public LspTarEntryStream(Stream inner, long length)
  {
    _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    _length = length;
    _remaining = length;
  }

  public override bool CanRead => true;

  public override bool CanSeek => false;

  public override bool CanWrite => false;

  public override long Length => _length;

  public override long Position
  {
    get => _length - _remaining;
    set => throw new NotSupportedException();
  }

  public override int Read(byte[] buffer, int offset, int count)
  {
    if (_remaining <= 0)
    {
      return 0;
    }

    int read = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
    _remaining -= read;
    return read;
  }

  public override int Read(Span<byte> buffer)
  {
    if (_remaining <= 0)
    {
      return 0;
    }

    int read = _inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
    _remaining -= read;
    return read;
  }

  /// <summary>Advances the archive past whatever the caller did not read.</summary>
  public void Drain()
  {
    SkipRemaining(_inner, _remaining);
    _remaining = 0;
  }

  public override void Flush()
  {
  }

  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

  public override void SetLength(long value) => throw new NotSupportedException();

  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

  private static void SkipRemaining(Stream stream, long count)
  {
    if (count <= 0)
    {
      return;
    }

    if (stream.CanSeek)
    {
      stream.Seek(count, SeekOrigin.Current);
      return;
    }

    byte[] scratch = new byte[Math.Min(count, 8192)];
    long remaining = count;
    while (remaining > 0)
    {
      int read = stream.Read(scratch, 0, (int)Math.Min(scratch.Length, remaining));
      if (read == 0)
      {
        return;
      }

      remaining -= read;
    }
  }
}
