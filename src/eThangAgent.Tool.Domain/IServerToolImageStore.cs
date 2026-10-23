using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>One generated image persisted into the workspace: the workspace-relative
///     path (what the transcript names and the model can hand to read_image) and the
///     absolute path on disk.</summary>
public sealed record SavedServerToolImage(string RelativePath, string FullPath);

/// <summary>Persists an image a SERVER-SIDE tool produced inside the provider's own
///     response (the openrouter:image_generation artifact, issue #230). A Tool Domain
///     seam for the same reason <see cref="IFileWriteAccess"/> is one: the domain
///     requests a capability and never touches System.IO itself.
/// <para>WHY A PATH AND NOT A MESSAGE PART: the artifact must be reachable by the user
///     (a file they can open) and by the model (read_image on a later turn). Attaching
///     the bytes to the conversation was rejected on measurement: a generated 1024x1024
///     PNG is roughly 1-2 MB, which is 1.3-2.7 M base64 characters — hundreds of
///     thousands of tokens, re-sent on EVERY later request. A path costs a dozen tokens
///     and the model can pull the bytes on demand. The surfaced line names the path, so
///     the conversation still records WHAT was produced.</para>
/// <para>Implementations must: create the artifact directory when absent (the file
///     write seam never does), validate the media type against the shared
///     image/png | image/jpeg rule by SNIFFING the decoded bytes (never the wire's
///     claim), enforce <see cref="ImageLimits.MaxBytes"/>, and refuse any path that
///     resolves outside the workspace. Failures are typed Results, never exceptions.</para></summary>
public interface IServerToolImageStore
{
  /// <summary>Persists one generated image and reports where it landed. A malformed
  ///     payload, an unsupported format, an oversized image, or a refused path is a
  ///     typed failure the caller surfaces to the model.</summary>
  Task<Result<SavedServerToolImage>> SaveAsync(string mediaType, string base64Data,
      CancellationToken ct = default);
}
