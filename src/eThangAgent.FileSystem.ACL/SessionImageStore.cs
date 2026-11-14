using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL;

/// <summary>The one store a session's images are persisted through: a server-side image tool's
///     artifact and a computer screenshot both land here, under one root, one folder per
///     session, one naming rule, and one admission rule.
/// <para>THE ROOT IS SHARED WITH THE SESSION-TRUSTED READ PATH. <see cref="ArtifactRoot"/> is
///     the folder the harness adds to its read-only trusted roots, so the model reaches a
///     stored image with read_image and the view opens it; the writer and the reader take the
///     folder from here, so they cannot disagree about where an image lives.</para>
/// <para>THE PATH IS ABSOLUTE ON PURPOSE: read_image reaches absolute paths anywhere on disk,
///     and the image is scratch - never a workspace file, never a database row.</para>
/// <para>A MISSING FILE IS STILL NOTHING TO THROW ABOUT: <see cref="ResolveAsync"/> reports a
///     typed failure naming the path, so the message that named it still reads and what is
///     missing is the picture.</para></summary>
public sealed class SessionImageStore : IImageStore
{
  /// <summary>The absolute root every session folder lives under, one path segment per part
  ///     so the spelling is the platform's. Spelled once: the writer creates session folders
  ///     beneath it and the read path widens the trusted roots by exactly this folder.</summary>
  public static string ArtifactRoot => ArtifactRootUnder(Path.GetTempPath());

  /// <summary><see cref="ArtifactRoot"/> under a caller-chosen base directory. Production takes
  ///     the operating system's temp directory; a test takes the run's own root so its scratch
  ///     state lands where the run cleans it up and nowhere else.</summary>
  /// <param name="baseDirectory">The directory the artifact root sits under.</param>
  public static string ArtifactRootUnder(string baseDirectory)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
    return Path.Combine(baseDirectory, "ethang-agent", "images");
  }

  private readonly string _sessionFolder;

  /// <summary>This store over the production root: the OS temp directory.</summary>
  /// <param name="sessionId">The session whose images these are.</param>
  public SessionImageStore(string? sessionId)
      : this(sessionId, Path.GetTempPath())
  {
  }

  /// <param name="sessionId">The session whose images these are: one folder per session, so
  ///     two conversations never overwrite each other's images. A blank id falls back to a
  ///     shared folder whose writes still never collide.</param>
  /// <param name="baseDirectory">The directory the artifact root sits under: the OS temp
  ///     directory in production, the run root in a test.</param>
  public SessionImageStore(string? sessionId, string baseDirectory)
  {
    string leaf = string.IsNullOrWhiteSpace(sessionId) ? "shared" : sessionId.Trim();
    _sessionFolder = Path.Combine(ArtifactRootUnder(baseDirectory), leaf);
  }

  public Task<Result<StoredImage>> PersistAsync(string mediaType, byte[] bytes,
      CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(mediaType);
    ArgumentNullException.ThrowIfNull(bytes);
    return SaveAsync(mediaType, bytes, ct);
  }

  public Task<Result<StoredImage>> PersistBase64Async(string mediaType, string base64Data,
      CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(mediaType);
    ArgumentNullException.ThrowIfNull(base64Data);
    byte[] bytes;
    try
    {
      bytes = Convert.FromBase64String(base64Data);
    }
    catch (FormatException ex)
    {
      return Result.Failure<StoredImage>(new DomainError("InvalidImagePayload",
          $"The image payload is not valid base64: {ex.Message}")).AsTask();
    }

    return SaveAsync(mediaType, bytes, ct);
  }

  /// <summary>Admits the bytes, names the file after the SNIFFED format, and writes it. The
  ///     name carries a timestamp and a random suffix, so two writes - even of identical
  ///     bytes, even in the same millisecond - are two files and the first is never
  ///     replaced.</summary>
  private async Task<Result<StoredImage>> SaveAsync(string mediaType, byte[] bytes,
      CancellationToken ct)
  {
    DomainError? refusal = ImagePayload.Refusal(mediaType, bytes);
    if (refusal is not null)
    {
      return Result.Failure<StoredImage>(refusal);
    }

    // The admission rule has already proven the bytes sniff as this media type.
    string name = ImagePayload.FileNameFor(DateTimeOffset.UtcNow, Guid.NewGuid(), mediaType);
    string fullPath = Path.Combine(_sessionFolder, name);
    try
    {
      _ = Directory.CreateDirectory(_sessionFolder);
      await File.WriteAllBytesAsync(fullPath, bytes, ct).ConfigureAwait(false);
      return Result.Success(new StoredImage(fullPath, name, mediaType));
    }
    catch (IOException ex)
    {
      return Result.Failure<StoredImage>(new DomainError("ImageUnwritable",
          $"The image could not be written to '{fullPath}': {ex.Message}"));
    }
    catch (UnauthorizedAccessException ex)
    {
      return Result.Failure<StoredImage>(new DomainError("ImageUnwritable",
          $"The image could not be written to '{fullPath}': {ex.Message}"));
    }
  }

  public async Task<Result<StoredImageContent>> ResolveAsync(string path,
      CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    try
    {
      if (!File.Exists(path))
      {
        return Result.Failure<StoredImageContent>(new DomainError("ImageNotFound",
            $"The stored image named '{path}' is not there; the message that named it still reads."));
      }

      byte[] bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
      string? sniffed = ImageFormats.SniffMediaType(bytes);
      return sniffed is null
          ? Result.Failure<StoredImageContent>(new DomainError("UnsupportedImageFormat",
              $"The file at '{path}' is neither PNG nor JPEG."))
          : Result.Success(new StoredImageContent(sniffed, bytes));
    }
    catch (IOException ex)
    {
      return Result.Failure<StoredImageContent>(new DomainError("ImageUnreadable",
          $"The stored image named '{path}' could not be read: {ex.Message}"));
    }
    catch (UnauthorizedAccessException ex)
    {
      return Result.Failure<StoredImageContent>(new DomainError("ImageUnreadable",
          $"The stored image named '{path}' could not be read: {ex.Message}"));
    }
  }
}
