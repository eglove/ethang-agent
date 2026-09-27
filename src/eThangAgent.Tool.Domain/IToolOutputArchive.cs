using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Archive seam for oversized tool results (the store-and-read-back context
///     policy). The Tool Domain owns the interface; the Storage ACL implements it —
///     the domain never knows SQL exists. Full content is stored once per workspace,
///     content-addressed by its SHA-256 (identical content is deduplicated); the
///     handle is byte-stable (<c>arch:</c> plus a SHA-256 hex prefix) so the
///     prompt-cache prefix stays stable across turns and sessions.</summary>
public interface IToolOutputArchive
{
  /// <summary>Stores the full content (deduplicated per workspace) and returns its
  ///     byte-stable handle. Empty content is rejected: an archive entry always
  ///     carries text to page back.</summary>
  Task<Result<string>> ArchiveAsync(string content, CancellationToken ct = default);

  /// <summary>Renders the head-and-tail excerpt that enters the conversation in place
  ///     of the full content: one stable marker line naming the byte-stable handle and
  ///     the omitted character count, then the head, an elision separator, and the
  ///     tail. A zero <paramref name="tailChars"/> renders the head only — the
  ///     error-result form, whose leading <c>Error [Code]: ...</c> line carries the
  ///     fault. The handle named in the marker is exactly what <see cref="ArchiveAsync"/>
  ///     returns for the same content (both derive it from the content's hash).
  ///     Content no longer than head + tail passes back unchanged — nothing to omit.</summary>
  string Excerpt(string content, int headChars, int tailChars);

  /// <summary>Reads one page of archived content back. Offsets and sizes are .NET
  ///     UTF-16 code units. The page reports its global 1-based line range so the
  ///     read-back tool can render the read tool's gutter. Unknown handles fail
  ///     ArchiveNotFound; an offset at or beyond the archived length fails
  ///     InvalidParameterValue naming the total.</summary>
  Task<Result<ArchivePage>> ReadBackAsync(string handle, int offset, int maxChars,
      CancellationToken ct = default);
}

/// <summary>One page of archived content: the slice, its position in the whole (chars
///     and global 1-based lines), and whether more content follows.</summary>
public sealed record ArchivePage(
    string Handle,
    long TotalChars,
    int Offset,
    string Text,
    int StartLine,
    int EndLine,
    bool HasMore);
