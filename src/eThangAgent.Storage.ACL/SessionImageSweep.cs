using System.Collections.Immutable;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Storage.ACL;

/// <summary>Removes a session's stored images that no persisted message names (the orphan
///     half of the file-plus-pointer form), and reports how many it removed.
///     <para>TWO OWNERS, ONE ANSWER. The paths a transcript names live in agent_messages
///     (this project); the files a session folder holds live behind
///     <see cref="IImageStore"/> (the FileSystem ACL). Neither side can decide an orphan alone:
///     the store cannot enumerate the folder, and the sink cannot read the rows. This class is the
///     seam that joins them and nothing else.</para>
///     <para>IT RUNS WHERE NO TURN CAN BE IN FLIGHT. A file is written BEFORE the message naming
///     it is persisted, so between those two moments the file is unreferenced while being
///     perfectly correct. Sweeping there would delete a picture the conversation is about to
///     record - which is why this is an entry point a caller invokes at a known rest point (a
///     resumed session opening), never a hook on the write path.</para>
///     <para>NEVER THROWS: a sweep is a cleanup, and a cleanup that cannot run reports a typed
///     failure rather than stopping the work it was tidying up after.</para></summary>
public static class SessionImageSweep
{
  /// <summary>Sweeps one session's image folder against the references its persisted transcript
  ///     names. The report is the count removed, so a caller can say what happened instead of
  ///     guessing; zero is the ordinary answer for a session that crashed nothing and for a
  ///     second run over one already swept.</summary>
  /// <param name="store">The store holding the session's transcript.</param>
  /// <param name="sessionId">The session whose images are swept - its folder is the unit.</param>
  /// <param name="images">The session's image store.</param>
  /// <param name="ct">Cancels the sweep.</param>
  public static async Task<Result<int>> SweepAsync(SqliteAgentStore store, AgentId sessionId,
      IImageStore images, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(store);
    ArgumentNullException.ThrowIfNull(images);

    Result<IReadOnlySet<string>> referenced = await store
        .GetStoredImagePathsAsync(sessionId, ct).ConfigureAwait(false);
    if (!referenced.IsSuccess)
    {
      // An unknown reference set is NOT an empty one: sweeping against it would delete every
      // picture the session has.
      return Result.Failure<int>(referenced.Error);
    }

    return await images.SweepAsync(referenced.Value, ct).ConfigureAwait(false);
  }

  /// <summary>The same sweep for a session with NO transcript row at all: every file in its
  ///     folder is an orphan by definition, because no message exists that could name one.
  ///     <para>THE EMPTY SET IS THE CORRECT INPUT HERE and only here. A session id whose row is
  ///     absent is a session that never recorded a message, so there is nothing a file could be
  ///     referenced by; that is a different fact from a transcript this code failed to read, which
  ///     is a failure and never an empty set.</para></summary>
  /// <param name="images">The session's image store.</param>
  /// <param name="ct">Cancels the sweep.</param>
  public static Task<Result<int>> SweepSessionWithNoTranscriptAsync(IImageStore images,
      CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(images);
    return images.SweepAsync(ImmutableHashSet<string>.Empty, ct);
  }
}
