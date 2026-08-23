using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Resolves tool-supplied paths against the workspace root and refuses
/// anything that resolves outside it. Segment-aware: a sibling directory whose name
/// merely shares a prefix with the root is correctly rejected.</summary>
public sealed class WorkspacePathResolver : IPathResolver
{
    private readonly string _root;

    public WorkspacePathResolver(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Workspace root must be a non-empty path.", nameof(root));
        _root = Path.GetFullPath(root);
    }

    public Result<string> Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Result<string>.Failure(new Error("InvalidPath",
                "'path' must be a non-empty string."));

        string candidate = Path.IsPathRooted(path) ? path : Path.Combine(_root, path);
        string full;
        try
        {
            full = Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<string>.Failure(new Error("InvalidPath",
                $"'path' could not be resolved: {ex.Message}"));
        }

        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !string.Equals(full, _root, StringComparison.Ordinal))
        {
            return Result<string>.Failure(new Error("PathOutsideWorkspace",
                $"'{path}' resolves to '{full}', which is outside the workspace '{_root}'. " +
                "Use a path inside the workspace."));
        }

        return Result<string>.Success(full);
    }
}
