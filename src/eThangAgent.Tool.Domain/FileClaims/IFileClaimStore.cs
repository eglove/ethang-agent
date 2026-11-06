namespace eThangAgent.ToolDomain.FileClaims;

/// <summary>The file-claim contract (issue #363): the Tool Domain owns this
/// contract; the daemon owns the shared store. Claims are session-keyed,
/// file-granular, and held under a short TTL with expiry computed on read
/// - never swept, so a dead holder self-clears
/// and no deadlock is possible. Renewal happens only on mutating-tool calls
/// (edit/write) by the holder; there is no background timer. Shell() bypasses
/// the file seam entirely and is out of scope: claims are a cooperative guard
/// among well-behaved tool users, not a lock.</summary>
public interface IFileClaimStore
{
  /// <summary>Acquires the claim for <paramref name="sessionId"/> when no live
  /// claim is held by another session. A live claim held by the same session is
  /// granted again with a refreshed expiry.</summary>
  FileClaimResult Acquire(string workspaceId, string path, string sessionId, DateTimeOffset now);

  /// <summary>Refreshes the expiry of the caller's own live claim. Renewal is
  /// per #358 option (a): mutating calls only, no timer.</summary>
  FileClaimResult Renew(string workspaceId, string path, string sessionId, DateTimeOffset now);

  /// <summary>Releases the caller's own claim. Returns false when the caller
  /// holds no live claim.</summary>
  bool Release(string workspaceId, string path, string sessionId, DateTimeOffset now);

  /// <summary>Reads the live claim without acquiring. Expired claims read as
  /// absent (expiry-on-read).</summary>
  FileClaim? Peek(string workspaceId, string path, DateTimeOffset now);
}

/// <summary>One live claim: the holding session and its expiry.</summary>
public sealed record FileClaim(string SessionId, DateTimeOffset Expiry);

/// <summary>The typed outcome of an acquire/renew: granted, or refused with the
/// holder session id and claim expiry so the tool layer can surface
/// <c>Error [Code]:</c> naming both.</summary>
public sealed record FileClaimResult(bool Granted, string? HolderSessionId, DateTimeOffset? Expiry);

/// <summary>Canonical error codes for claim refusals.</summary>
public static class FileClaimErrorCodes
{
  /// <summary>A file is already claimed by another session.</summary>
  public const string FileClaimHeld = "FileClaimHeld";

  /// <summary>The caller's own claim lapsed before renewal.</summary>
  public const string FileClaimExpired = "FileClaimExpired";
}

/// <summary>The claim window (issue #358, option (a)): 15 minutes, expiry
/// computed on read, never swept. A named constant - widening it is a product
/// decision that updates this constant and its test in the same change.</summary>
public static class FileClaimPolicy
{
  /// <summary>How long a claim stays live without renewal: fifteen minutes.</summary>
  public static TimeSpan ClaimTtl { get; } = TimeSpan.FromMinutes(15);
}
