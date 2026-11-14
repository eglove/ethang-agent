using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>One image persisted for a session: where it landed on disk and what it is.
///     <para><see cref="Path"/> is ABSOLUTE, always. It is the image's identity in a
///     transcript - the model hands it to read_image and the view opens it - so a relative
///     path would name nothing.</para></summary>
/// <param name="Path">The absolute path of the stored image.</param>
/// <param name="FileName">The stored image's file name, for display.</param>
/// <param name="MediaType">The media type SNIFFED from the stored bytes, which the path's
///     extension agrees with.</param>
public sealed record StoredImage(string Path, string FileName, string MediaType);

/// <summary>An image read back through the store: the sniffed media type and the bytes.
///     A missing file is never this shape - it is a typed failure.</summary>
/// <param name="MediaType">The media type SNIFFED from the bytes that were read.</param>
/// <param name="Bytes">The stored image's bytes.</param>
public sealed record StoredImageContent(string MediaType, ReadOnlyMemory<byte> Bytes);

/// <summary>The one store a session's images are persisted through, whichever producer made
///     them - a server-side image tool's artifact, or a computer screenshot. There is exactly
///     one implementation of "save an image for a session", so both producers land in the same
///     place, under the same naming and the same admission rule.
/// <para>WHERE AN IMAGE LIVES: the operating system's temp directory, one folder per session,
///     never the user's workspace and never the app's database. The report is an absolute path
///     under that root, so the model reaches an image with read_image and the view opens it.
///     Nothing is written into a directory the harness was merely pointed at.</para>
/// <para>WHAT AN IMPLEMENTATION GUARANTEES: the folder is created when absent; the admission
///     rule is <see cref="ImagePayload.Refusal"/> on the decoded bytes, so the format is
///     sniffed and never trusted; the file's extension follows the sniffed format; a name is
///     unique per write, so two writes never overwrite each other; and every failure -
///     malformed payload, oversized image, unwritable file, gone file - is a typed
///     <see cref="Result{T}"/>, never an exception and never empty bytes as success.</para></summary>
public interface IImageStore
{
  /// <summary>Persists one image and reports where it landed.</summary>
  /// <param name="mediaType">The media type the producer claims; the bytes decide.</param>
  /// <param name="bytes">The image payload, decoded.</param>
  /// <param name="ct">Cancels the write.</param>
  Task<Result<StoredImage>> PersistAsync(string mediaType, byte[] bytes, CancellationToken ct = default);

  /// <summary>Persists one image supplied as base64 text - the form a provider wire and a
  ///     tool result both carry. Invalid base64 is a typed failure, not an exception.</summary>
  /// <param name="mediaType">The media type the producer claims; the bytes decide.</param>
  /// <param name="base64Data">The image payload, base64-encoded.</param>
  /// <param name="ct">Cancels the write.</param>
  Task<Result<StoredImage>> PersistBase64Async(string mediaType, string base64Data, CancellationToken ct = default);

  /// <summary>Reads back the image a reference names. A path whose file is gone, or whose bytes
  ///     are not an image, is a typed failure naming the path - the message that named it still
  ///     reads, and what is missing is the picture.</summary>
  /// <param name="path">The absolute path of a persisted image.</param>
  /// <param name="ct">Cancels the read.</param>
  Task<Result<StoredImageContent>> ResolveAsync(string path, CancellationToken ct = default);

  /// <summary>Removes every file in this session's folder that <paramref name="referencedPaths"/>
  ///     does not name, and reports how many it removed.
  ///     <para>AN ORPHAN IS A FILE NO PERSISTED MESSAGE NAMES - that and nothing else. A file
  ///     written moments ago is not an orphan while the turn that wrote it is still going to
  ///     persist the message naming it, so the caller runs this only where no turn for this
  ///     session can be in flight.</para>
  ///     <para>AGE IS NEVER THE TEST: a timestamp cannot distinguish a file a live turn produced
  ///     from one a killed process abandoned, and deleting the first is losing a picture the
  ///     conversation is about to record.</para>
  ///     <para>A folder that does not exist reports zero rather than failing - nothing was
  ///     written, so there is nothing to sweep. A second run over an already-swept folder
  ///     reports zero for the same reason: the removal leaves nothing to find.</para></summary>
  /// <param name="referencedPaths">Every stored-image path a persisted message names. The
  ///     comparison is case-insensitive: two spellings of one Windows path are the same file.</param>
  /// <param name="ct">Cancels the sweep.</param>
  Task<Result<int>> SweepAsync(IReadOnlySet<string> referencedPaths, CancellationToken ct = default);
}
