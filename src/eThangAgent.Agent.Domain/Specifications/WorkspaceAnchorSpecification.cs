using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Specifications;

/// <summary>Spawn-time workspace anchor rule (worktree ladder, T5): a requested anchor
///     must be a non-empty absolute path whose directory exists — else the AnchorMissing
///     code-path — and must resolve INSIDE the parent's effective root — else AnchorInvalid.
///     Containment is case-insensitive and segment-aware, mirroring
///     <see cref="ToolDomain.WorkspacePathResolver"/>'s NormalizeRoot discipline: both
///     roots are normalized exactly like it (Path.GetFullPath, trailing separators trimmed,
///     drive-root separator restored) and containment requires
///     full.StartsWith(parentRoot + Path.DirectorySeparatorChar, OrdinalIgnoreCase) or
///     full equals parentRoot — never a raw string prefix, so a sibling directory whose
///     name merely shares the root's prefix is rejected. The spec reports violations with
///     the code embedded in the message prefix; the HANDLER pattern-matches the prefix and
///     surfaces exactly <c>AnchorMissing</c> / <c>AnchorInvalid</c> as the DomainError code.</summary>
/// <param name="parentRoot">The effective root containment is measured against: the
///     parent's own persisted anchor when it carries one, else the session workspace.</param>
public sealed class WorkspaceAnchorSpecification(string parentRoot) : Specification<string>
{
  public const string MissingPrefix = "AnchorMissing:";
  public const string InvalidPrefix = "AnchorInvalid:";

  public override bool IsSatisfiedBy(string candidate)
      => ViolationMessageFor(candidate) is null;

  protected override string FailureMessageFor(string candidate)
      => ViolationMessageFor(candidate) ?? string.Empty;

  private string? ViolationMessageFor(string candidate)
  {
    if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathRooted(candidate))
    {
      return MissingPrefix + " the anchor '" + candidate + "' must be a non-empty absolute path.";
    }

    if (!Directory.Exists(candidate))
    {
      return MissingPrefix + " the anchor '" + candidate + "' does not exist.";
    }

    string full;
    try
    {
      full = Path.GetFullPath(candidate);
    }
    catch (Exception ex) when (
        ex is ArgumentException or NotSupportedException or PathTooLongException)
    {
      return InvalidPrefix + " the anchor '" + candidate + "' could not be resolved: " + ex.Message;
    }

    string root = NormalizeRoot(parentRoot);
    return !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
      ? InvalidPrefix + " the anchor '" + full + "' resolves outside the parent root '" + root + "'."
      : null;
  }

  /// <summary>Canonical form of the parent root: fully qualified with trailing separators
  ///     removed — byte-identical to WorkspacePathResolver.NormalizeRoot, so candidate
  ///     comparisons never differ by separator or case. A drive root ("C:\") would trim
  ///     to "C:" which no longer refers to the drive root; restore the separator so it
  ///     stays meaningful.</summary>
  private static string NormalizeRoot(string root)
  {
    string full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    return full.Length == 2 && full[1] == ':' ? full + Path.DirectorySeparatorChar : full;
  }
}
