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
///     path resolver decorator consumes. Never null; empty when nothing is approved.</summary>
public sealed record ResolvedTrustedDirectories(IReadOnlyList<TrustedDirectory> List);
