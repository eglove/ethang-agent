using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Decorates a workspace-rooted resolver with the read tool's path policy:
/// relative paths delegate to the inner resolver (whose containment rule keeps them
/// bound to the workspace root — '..' cannot escape), while absolute paths pass
/// through from anywhere on disk. Read is read-only, so an absolute path needs no
/// confinement; mutating tools keep the strict resolver. Malformed absolute input
/// fails with the same InvalidPath contract as the other resolvers.</summary>
public sealed class RootAnchoredPathResolver(IPathResolver inner) : IPathResolver
{
  private readonly IPathResolver _inner = inner ?? throw new ArgumentNullException(nameof(inner));

  public Result<string> Resolve(string path) =>
      string.IsNullOrWhiteSpace(path)
          ? Result.Failure<string>(new DomainError("InvalidPath",
              "'path' must be a non-empty string."))
          : ResolveRooted(path);

  private Result<string> ResolveRooted(string path) =>
      Path.IsPathRooted(path)
          ? UnrootedPathResolver.Instance.Resolve(path)
          : _inner.Resolve(path);
}
