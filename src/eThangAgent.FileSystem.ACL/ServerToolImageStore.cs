using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

// Named decision (CA1849): the artifact write is one small local file; sync IO inside
// the async wrapper keeps the path simple without meaningful thread blocking.
#pragma warning disable CA1849 // Call async methods when in an async method

namespace eThangAgent.FileSystem.ACL;

/// <summary>Persists a server-side tool's generated image into the operating system's temp
///     directory, under a per-session folder. The artifact is SCRATCH, not durable state and
///     not a workspace file: the harness adds nothing to a directory it was merely pointed at,
///     and nothing outside its database that the user has to clean up.
/// <para>THE PATH IS ABSOLUTE ON PURPOSE. read_image reaches absolute paths anywhere on disk,
///     so the model can pull the artifact on a later turn; a workspace-relative path would
///     only work if the file were inside the workspace, which is exactly what this avoids.
///     A model that wants the image kept copies it into the workspace with its ordinary tools.
///     </para>
/// <para>Format validation SNIFFS the decoded bytes through
///     <see cref="ImageFormats.SniffMediaType"/> rather than trusting the wire's media type
///     claim, and the file extension follows the sniffed format. Size is bounded by
///     <see cref="ImageLimits.MaxBytes"/>. Every refusal is a typed Result.</para></summary>
public sealed class ServerToolImageStore : IServerToolImageStore
{
  /// <summary>The folder under the temp directory that holds every session's artifacts.</summary>
  public const string ArtifactFolderName = "ethang-agent/images";

  /// <summary>The absolute artifact root. Spelled once: the writer creates session folders
  ///     under it and the read path is widened by exactly this folder, so the two cannot
  ///     disagree about where a generated image lives.</summary>
  public static string ArtifactRoot => Path.Combine(Path.GetTempPath(), ArtifactFolderName);

  private readonly string _sessionFolder;

  /// <param name="sessionId">The session whose artifacts these are: one folder per session,
  ///     so two conversations never overwrite each other's images and a user can find one
  ///     session's output at a glance. A blank id falls back to a shared folder.</param>
  public ServerToolImageStore(string? sessionId)
  {
    string leaf = string.IsNullOrWhiteSpace(sessionId) ? "shared" : sessionId.Trim();
    _sessionFolder = Path.Combine(ArtifactRoot, leaf);
  }

  public async Task<Result<SavedServerToolImage>> SaveAsync(string mediaType, string base64Data,
      CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(mediaType);
    ArgumentNullException.ThrowIfNull(base64Data);
    if (base64Data.Length == 0)
    {
      return Result.Failure<SavedServerToolImage>(new DomainError("InvalidImagePayload",
          "The generated image carried no payload."));
    }

    byte[] bytes;
    try
    {
      bytes = Convert.FromBase64String(base64Data);
    }
    catch (FormatException ex)
    {
      return Result.Failure<SavedServerToolImage>(new DomainError("InvalidImagePayload",
          $"The generated image payload is not valid base64: {ex.Message}"));
    }

    if (bytes.Length > ImageLimits.MaxBytes)
    {
      return Result.Failure<SavedServerToolImage>(new DomainError("ImageTooLarge",
          $"The generated image is {bytes.Length} bytes; the maximum is {ImageLimits.MaxBytes} bytes (20 MB)."));
    }

    // The BYTES decide the format, never the wire's claim.
    string? sniffed = ImageFormats.SniffMediaType(bytes);
    if (sniffed is null)
    {
      return Result.Failure<SavedServerToolImage>(new DomainError("UnsupportedImageFormat",
          $"The generated image is neither PNG nor JPEG (the wire claimed '{mediaType}')."));
    }

    string name = FileNameFor(sniffed);
    string fullPath = Path.Combine(_sessionFolder, name);
    try
    {
      _ = Directory.CreateDirectory(_sessionFolder);
      await File.WriteAllBytesAsync(fullPath, bytes, ct).ConfigureAwait(false);
      return Result.Success(new SavedServerToolImage(fullPath, name));
    }
    catch (IOException ex)
    {
      return Result.Failure<SavedServerToolImage>(new DomainError("ImageUnwritable",
          $"The generated image could not be written to '{fullPath}': {ex.Message}"));
    }
    catch (UnauthorizedAccessException ex)
    {
      return Result.Failure<SavedServerToolImage>(new DomainError("ImageUnwritable",
          $"The generated image could not be written to '{fullPath}': {ex.Message}"));
    }
  }

  /// <summary>A collision-free artifact name: the timestamp plus a random suffix, so two
  ///     generations in the same millisecond never overwrite each other.</summary>
  private static string FileNameFor(string mediaType)
  {
    string extension = mediaType == "image/jpeg" ? "jpg" : "png";
    string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff",
        System.Globalization.CultureInfo.InvariantCulture);
    return $"generated-{stamp}-{Guid.NewGuid():N}.{extension}";
  }
}
