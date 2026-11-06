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
///     <para><see cref="WithGeneratedArtifacts"/> widens the list by the harness's OWN
///     generated-image folder under the temp directory. That folder is not a user approval:
///     it is where the harness puts an image a server-side tool produced, and read_image has
///     to reach it or the model can never open an image it generated. It is added here, beside
///     the approvals, because it is the same seam - one list the resolver decorator consumes,
///     with one place that says what is in it.</para></summary>
public sealed record ResolvedTrustedDirectories(IReadOnlyList<TrustedDirectory> List)
{
  /// <summary>This list plus the generated-artifact folder, read-only. Idempotent: a list that
  ///     already carries the folder is returned unchanged.</summary>
  public ResolvedTrustedDirectories WithGeneratedArtifacts()
  {
    string folder = GeneratedArtifactFolder;
    return List.Any(directory => string.Equals(directory.Root, folder, StringComparison.OrdinalIgnoreCase))
        ? this
        : new([.. List, new TrustedDirectory(folder)]);
  }

  /// <summary>The absolute folder the harness writes generated images into, taken from the
  ///     store that writes them so the writer and the reader cannot disagree about it.</summary>
  public static string GeneratedArtifactFolder => ServerToolImageStore.ArtifactRoot;
}
