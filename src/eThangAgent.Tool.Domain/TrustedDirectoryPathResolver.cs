using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>One user-approved external root (issue #96): <paramref name="Root"/> is the
/// directory, <paramref name="Editable"/> says whether mutating tools may write inside
/// it. Read access is implied for every trusted root - that is what approving one
/// grants - so a read-only root is still fully usable with the read tool, whose
/// absolute-path policy already reaches anywhere on disk.</summary>
public sealed record TrustedDirectory(string Root, bool Editable = false);

/// <summary>Widens a tool's path resolution with the session's user-approved external
/// roots (issue #96). The seam exists because a recurring external root - a personal
/// vault, a shared docs tree - had no standard tool surface: the only workaround was
/// raw <c>System.IO</c> inside <c>exec</c>, which bypasses the read tool's line ranges,
/// gutter format, and freshness policy. One approval per root makes the ordinary
/// tools work there.
/// <para>DECORATOR, not a replacement: <see cref="Resolve"/> asks the wrapped resolver
/// FIRST and returns its success unchanged, so workspace (or unrooted) semantics are
/// exactly what they were. Only a REFUSED path is reconsidered, and only against the
/// trusted roots - which is what keeps this a widening and never a change: with no
/// roots approved the type is byte-identical to the resolver it wraps, including the
/// error text.</para>
/// <para>Write policy: a root approved read-only accepts reads and refuses writes with
/// the distinct <c>TrustedRootReadOnly</c> code, which tells the model precisely what
/// to do (read there is fine; an edit needs the user's approval). Read-only tools pass
/// <c>enforceWritePolicy: false</c> through <c>ForReads</c>, so a read is never refused
/// as if it were a write. Deny by default in the other direction too: a path that is in
/// no trusted root keeps the wrapped resolver's own refusal, now enriched with the
/// approved list so the model learns what IS reachable instead of guessing.</para>
/// <para>Roots are normalized ONCE at construction (fully qualified, trailing
/// separators stripped) and compared case-insensitively, exactly as the workspace
/// resolver compares the workspace: a picked folder that arrives with a trailing
/// separator must not flip every containment check.</para>
/// <para>SESSION GRANTS widen the same set: a path the user quoted in a message is
/// granted for the session, read-only, so the two sources are one reach list with one
/// place to consult. The grant set is LIVE rather than snapshotted - a grant made
/// between two resolutions must apply to the second - so it is read on every
/// resolution instead of being copied at construction.</para>
/// <para>NAMED DECISION - anchoring drops the trusted set. The anchoring seam (a scoped
/// tool's RootedAt) builds a bare resolver at the anchor, so an anchored child never
/// inherits its parent's external approvals: an anchor is a narrower confinement
/// boundary by construction, and silent inheritance would let a spawn widen its own
/// reach.</para></summary>
public sealed class TrustedDirectoryPathResolver : IPathResolver
{
  private readonly IPathResolver _inner;
  private readonly bool _enforceWritePolicy;
  private readonly SessionPathGrants? _grants;

  /// <param name="inner">The resolver this one widens. Consulted FIRST, always.</param>
  /// <param name="trusted">User-approved external roots, already parsed.</param>
  /// <param name="enforceWritePolicy">True for MUTATING tools: a trusted root that is
  ///     not editable refuses writes. False for read-only tools, which never write, so
  ///     the permission cannot matter to them.</param>
  /// <param name="grants">The session's quoted-path grants, or null for none. Read on every
  ///     resolution, so a grant made after construction still applies.</param>
  public TrustedDirectoryPathResolver(IPathResolver inner, IReadOnlyList<TrustedDirectory>? trusted = null,
      bool enforceWritePolicy = true, SessionPathGrants? grants = null)
  {
    _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    _enforceWritePolicy = enforceWritePolicy;
    _grants = grants;
    TrustedRoots = [.. (trusted ?? []).Select(t => t with { Root = TrustedRootPath.Normalize(t.Root) })];
  }

  /// <summary>Every root this resolver accepts: the approved list first, then the session's
  ///     quoted-path grants. Empty when nothing is approved and nothing was quoted.</summary>
  public IReadOnlyList<TrustedDirectory> ReachableRoots =>
      _grants is null ? TrustedRoots : [.. TrustedRoots, .. _grants.List];

  /// <summary>The normalized trusted roots, in configured order.</summary>
  public IReadOnlyList<TrustedDirectory> TrustedRoots { get; }

  /// <summary>This resolver for a READ-ONLY tool: identical inner resolver and roots, with
  ///     the per-root write policy switched off. Idempotent, so a caller can ask
  ///     unconditionally. A read-only tool never writes, so the permission cannot matter
  ///     to it - and enforcing it there would refuse a read the user did approve (see
  ///     <see cref="ActionScopedPathResolver"/> for the container tools, whose actions
  ///     flip between reading and writing).</summary>
  public TrustedDirectoryPathResolver AsReadOnlyResolver() =>
      _enforceWritePolicy ? new(_inner, TrustedRoots, enforceWritePolicy: false, _grants) : this;

  public Result<string> Resolve(string path)
  {
    Result<string> resolved = _inner.Resolve(path);
    // The wrapped resolver owns every path it accepts, and its refusal is the only
    // case the trusted roots may answer - never a reinterpretation of a success.
    IReadOnlyList<TrustedDirectory> roots = ReachableRoots;
    return resolved.IsSuccess || roots.Count == 0 ? resolved : ResolveTrusted(path, resolved, roots);
  }

  /// <summary>Reconsiders a REFUSED path against the approved roots. A RELATIVE path
  ///     cannot be rescued (relative paths belong to the workspace, and one that escaped
  ///     it stays refused), and neither can a malformed one - both keep the wrapped
  ///     refusal verbatim, because a bad path is bad everywhere.</summary>
  private Result<string> ResolveTrusted(string path, Result<string> refused, IReadOnlyList<TrustedDirectory> roots)
  {
    if (!Path.IsPathRooted(path))
    {
      return refused;
    }

    string full;
    try
    {
      full = Path.GetFullPath(path);
    }
    catch (Exception ex) when (
        ex is ArgumentException or NotSupportedException or PathTooLongException)
    {
      return refused;
    }

    TrustedDirectory? match = roots.FirstOrDefault(d => TrustedRootPath.Contains(d.Root, full));
    return match is null ? EnrichRefusal(refused, full, roots) : Accept(path, full, match);
  }

  /// <summary>Accepts a path inside an approved root, or refuses it when the root is
  ///     read-only and this resolver enforces the write policy.</summary>
  private Result<string> Accept(string path, string full, TrustedDirectory directory) =>
      _enforceWritePolicy && !directory.Editable
          ? Result.Failure<string>(new DomainError("TrustedRootReadOnly",
              $"'{path}' resolves to '{full}', inside the trusted directory '{directory.Root}', " +
              "which is approved for READING only. Read it with the read tool; writing " +
              "there needs the user to approve that directory as editable."))
          : Result.Success(full);

  /// <summary>Keeps the wrapped refusal (its CODE is the contract the model and the
  ///     tests key on) and appends what IS approved, so the model learns the reachable
  ///     list from the failure instead of guessing at it. Refusals that are not
  ///     PathOutsideWorkspace - InvalidPath, a binary-file refusal, anything else - pass
  ///     through untouched: they were never about containment.</summary>
  private static Result<string> EnrichRefusal(Result<string> refused, string full,
      IReadOnlyList<TrustedDirectory> roots)
  {
    if (refused.Error is not { Code: "PathOutsideWorkspace" } error)
    {
      return refused;
    }

    string approved = string.Join("; ", roots.Select(t =>
        $"'{t.Root}' " + (t.Editable ? "read/write" : "read-only")));
    return Result.Failure<string>(error with
    {
      Message = error.Message + $" Trusted directories approved for this workspace ({approved})"
          + $": a path inside one of those is accepted, and '{full}' is in none.",
    });
  }
}
