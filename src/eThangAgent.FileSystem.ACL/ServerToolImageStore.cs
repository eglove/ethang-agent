using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

// Named decision (CA1849): the artifact write is one small local file; sync IO inside
// the async wrapper keeps the path simple without meaningful thread blocking.
#pragma warning disable CA1849 // Call async methods when in an async method

namespace eThangAgent.FileSystem.ACL;

/// <summary>Persists a server-side tool's generated image into the workspace (issue
///     #230). The artifact directory is <c>.ethang/images/</c> under the workspace root:
///     a RECORDED RULING that a generated image is a workspace artifact rather than
///     app-owned durable state — the user opens it, references it, and deletes it with
///     the rest of their working tree, and the model reaches it through the ordinary
///     read_image tool. The directory is created on demand because the file write seam
///     deliberately never creates parents.
/// <para>Format validation SNIFFS the decoded bytes through
///     <see cref="ImageFormats.SniffMediaType"/> rather than trusting the wire's media
///     type claim, and the file extension follows the sniffed format. Size is bounded by
///     <see cref="ImageLimits.MaxBytes"/>. Every refusal is a typed Result.</para></summary>
public sealed class ServerToolImageStore(IPathResolver resolver, IFileWriteAccess files) : IServerToolImageStore
{
  /// <summary>The artifact directory, workspace-relative and forward-slashed (the
  ///     transcript names paths in this form so they read the same everywhere).</summary>
  public const string ArtifactDirectory = ".ethang/images";

  private readonly IPathResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
  private readonly IFileWriteAccess _files = files ?? throw new ArgumentNullException(nameof(files));

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

    string relative = ArtifactDirectory + "/" + FileNameFor(sniffed);
    Result<string> resolved = _resolver.Resolve(relative);
    if (!resolved.IsSuccess)
    {
      return Result.Failure<SavedServerToolImage>(resolved.Error);
    }

    string fullPath = resolved.Value;
    string? directory = Path.GetDirectoryName(fullPath);
    if (directory is not null)
    {
      _ = Directory.CreateDirectory(directory);
    }

    Result<FileWriteOutcome> written = await _files
        .WriteFileBytesAsync(fullPath, bytes, overwrite: false, ct).ConfigureAwait(false);
    return written.IsSuccess
        ? Result.Success(new SavedServerToolImage(relative, fullPath))
        : Result.Failure<SavedServerToolImage>(written.Error);
  }

  /// <summary>A collision-free artifact name: the timestamp plus a random suffix, so two
  ///     generations in the same millisecond never overwrite each other (the write seam
  ///     refuses overwrites, and a refused write would lose the artifact).</summary>
  private static string FileNameFor(string mediaType)
  {
    string extension = mediaType == "image/jpeg" ? "jpg" : "png";
    string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff",
        System.Globalization.CultureInfo.InvariantCulture);
    return $"generated-{stamp}-{Guid.NewGuid():N}.{extension}";
  }
}
