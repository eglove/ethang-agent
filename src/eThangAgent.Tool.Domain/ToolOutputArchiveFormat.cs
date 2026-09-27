using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace eThangAgent.ToolDomain;

/// <summary>Pure contract for the archive store-and-read-back shape: byte-stable
///     handle derivation, the excerpt marker line, and the excerpt body. Every
///     consumer (the loop's archiving path, the read-back tool, both implementations
///     and fakes) renders through these members so the format is pinned in exactly
///     one place. All members are static and pure — no I/O, no state.</summary>
public static class ToolOutputArchiveFormat
{
  /// <summary>Handle prefix. Verbatim contract: handles are <c>arch:</c> plus the
  ///     first 16 hex characters of the content's SHA-256 — byte-stable so the
  ///     prompt-cache prefix stays stable.</summary>
  public const string HandlePrefix = "arch:";

  /// <summary>Hex characters of the SHA-256 carried in a handle.</summary>
  public const int HandleHashLength = 16;

  /// <summary>Length of the elision separator inserted between head and tail.</summary>
  public const int SeparatorLength = 1;

  /// <summary>Derives the byte-stable handle for content: <c>arch:</c> plus the first
  ///     16 hex characters of its SHA-256. Pure — the Storage ACL stores under the
  ///     same key, so a handle read back always resolves.</summary>
  public static string HandleOf(string content)
  {
    ArgumentNullException.ThrowIfNull(content);
    byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
    // Named decision (CA1308): lowercase hex is the by-design handle shape — the
    // excerpt marker and read-back contract quote lowercase handles verbatim.
#pragma warning disable CA1308 // Normalize strings to uppercase
    return HandlePrefix + Convert.ToHexString(hash)[..HandleHashLength].ToLowerInvariant();
#pragma warning restore CA1308 // Normalize strings to uppercase
  }

  /// <summary>True when the handle is well-formed for this contract: the prefix plus
  ///     exactly <see cref="HandleHashLength"/> lowercase hex characters.</summary>
  public static bool IsWellFormedHandle(string handle)
  {
    if (handle is null || !handle.StartsWith(HandlePrefix, StringComparison.Ordinal))
    {
      return false;
    }

    string hash = handle[HandlePrefix.Length..];
    return hash.Length == HandleHashLength && hash.All(IsLowerHex);
  }

  /// <summary>The stable marker line that opens every excerpt: names the byte-stable
  ///     handle and the omitted character count. Verbatim contract — the read-back
  ///     tool's documentation quotes this shape, and the loop's tests pin it.</summary>
  public static string MarkerLine(string handle, int omittedChars)
  {
    ArgumentNullException.ThrowIfNull(handle);
    return string.Create(CultureInfo.InvariantCulture,
        $"[tool-output archived: {handle} | {omittedChars.ToString(CultureInfo.InvariantCulture)} chars omitted | read back with the tool_output_read tool]");
  }

  /// <summary>The elision separator between head and tail.</summary>
  public const string Separator = "…";

  /// <summary>Renders the head-and-tail excerpt body: head, separator, tail. A zero
  ///     <paramref name="tailChars"/> renders the head only (the error-result form —
  ///     the leading <c>Error [Code]: ...</c> line carries the fault). Content no
  ///     longer than head + tail passes back unchanged: nothing to omit, no marker.</summary>
  public static string ExcerptBody(string content, int headChars, int tailChars)
  {
    ArgumentNullException.ThrowIfNull(content);
    return FitsWithin(content, headChars, tailChars) ? content : HeadTail(content, headChars, tailChars);
  }

  /// <summary>Head-and-tail render for content already known to overflow; kept out
  /// of ExcerptBody so neither style rule fires (IDE0046 wants the early return
  /// folded, S3358 forbids nesting the two ternaries).</summary>
  private static string HeadTail(string content, int headChars, int tailChars)
  {
    return tailChars == 0
      ? content[..headChars]
      : string.Create(CultureInfo.InvariantCulture,
          $"{content[..headChars]}{Separator}{content[^tailChars..]}");
  }

  /// <summary>True when the content fits within head + tail and must not be archived.</summary>
  public static bool FitsWithin(string content, int headChars, int tailChars)
  {
    ArgumentNullException.ThrowIfNull(content);
    return content.Length <= headChars + tailChars;
  }

  private static bool IsLowerHex(char c)
      => c is (>= '0' and <= '9') or (>= 'a' and <= 'f');
}
