using eThangAgent.FileSystem.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition;

/// <summary>Per-container holder for the stored trusted-directory list (issue #96), the
/// same construction-phase glue <see cref="SessionFilesCarrier"/> and
/// <see cref="SkillDirectoriesCarrier"/> are: the factory fills it after its preference
/// read and the RemoteHost block overlays the value onto the settings JSON the host
/// receives - so the host process (which runs EVERY production turn) resolves exactly
/// the approved roots the app-side session does.
/// <para>Mutable by design: it is construction-phase glue, filled once before the
/// container hands the session out.</para></summary>
public sealed class TrustedDirectoriesCarrier
{
  public string? Stored { get; private set; }

  public void Fill(string? stored) => Stored = stored;
}

/// <summary>The parsed trusted roots registered per container: the input the
///     path resolver decorator consumes. Never null; empty when nothing is approved.
///     <para><see cref="WithStoredImages"/> widens the list by the harness's OWN image folder
///     under the temp directory - the one <see cref="SessionImageStore"/> writes. That folder
///     is not a user approval: an image is persisted there whether a server-side tool produced
///     it or a screenshot captured it, and read_image has to reach it or the model can never
///     open an image it made. It is added here, beside the approvals, because it is the same
///     seam - one list the resolver decorator consumes, with one place that says what is in
///     it.</para></summary>
public sealed record ResolvedTrustedDirectories(IReadOnlyList<TrustedDirectory> List)
{
  /// <summary>This list plus the stored-image folder, read-only. Idempotent: a list that
  ///     already carries the folder is returned unchanged.</summary>
  public ResolvedTrustedDirectories WithStoredImages()
  {
    string folder = StoredImageFolder;
    return List.Any(directory => string.Equals(directory.Root, folder, StringComparison.OrdinalIgnoreCase))
        ? this
        : new([.. List, new TrustedDirectory(folder)]);
  }

  /// <summary>The absolute folder the harness writes session images into, taken from the store
  ///     that writes them so the writer and the reader cannot disagree about it.</summary>
  public static string StoredImageFolder => SessionImageStore.ArtifactRoot;
}
