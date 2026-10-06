using System.Globalization;

namespace eThangAgent.ToolDomain;

/// <summary>The content-true freshness state of a file at the moment it was read:
///     the (Length, LastWriteTimeUtc) pair. The pair is the invalidation protocol —
///     any write (grow, shrink, in-place same-length) changes one of the two, so
///     stale cached ranges drop wholesale. <see cref="Token"/> is a short stable
///     hex digest of the pair the read tool's annotation shows; the model compares
///     tokens across reads. Captured by the file-system ACL at the read moment —
///     stat-first, before reading lines — never by a later stat.</summary>
public sealed record FileVersion(long Length, DateTime LastWriteTimeUtc)
{
  /// <summary>Stable token: 'v' + 8 hex chars. Deterministic for the same pair across
  ///     runs (no random salt), so the model can compare tokens across a whole turn.</summary>
  public string Token
  {
    get
    {
      ulong h = 14695981039346656037UL; // FNV-1a offset basis
      foreach (byte b in BitConverter.GetBytes(Length))
      {
        h = (h ^ b) * 1099511628211UL;
      }

      foreach (byte b in BitConverter.GetBytes(LastWriteTimeUtc.Ticks))
      {
        h = (h ^ b) * 1099511628211UL;
      }

      h *= 0xbf58476d1ce4e5b9; // splitmix64 finalizer (first two steps)
      h ^= h >> 31;
      h *= 0x94d049bb133111eb;
      h ^= h >> 33;
      return "v" + (h >> 32).ToString("x8", CultureInfo.InvariantCulture);
    }
  }
}
