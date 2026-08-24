using System.Text;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed class WorkingDiffTool : ITool
{
    /// <summary>The character cap at which the access layer truncates patches. The
    /// domain owns this contract number for display; the access layer enforces it.</summary>
    public const int PatchCharCap = 20000;

    private readonly IPathResolver _resolver;
    private readonly IGitQueryAccess _git;

    public ToolDefinition Definition { get; } = new(
        "working_diff",
        "Show the working-tree diff of the repository at the workspace root, bounded. " +
        "timeoutSeconds and scope are mandatory; scope is exactly one of 'Staged' (index vs HEAD), 'Unstaged' " +
        "(worktree vs index), or 'All'; path optionally narrows to a single path inside " +
        "the workspace. The patch is cut at 20000 characters with a visible [warning] " +
        "line when anything was dropped — narrow with path/scope to see the rest. Output " +
        "begins with an annotation line `[working-diff scope=<scope> path=<path|none>: " +
        "N file(s), +A/-D lines]` followed by the patch verbatim; no changes reports " +
        "`[working-diff ...: no differences]`. Errors begin with `Error [Code]:`.",
        [
            new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.Integer, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("scope", ToolParameterType.String,
                "Exactly 'Staged', 'Unstaged', or 'All' (case-sensitive)."),
            new ToolParameter("path", ToolParameterType.String,
                "Optional single-path filter, workspace-relative or absolute-inside-workspace."),
        ],
        ["timeoutSeconds", "scope"]);

    public WorkingDiffTool(IPathResolver resolver, IGitQueryAccess git)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _git = git ?? throw new ArgumentNullException(nameof(git));
    }

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
        var parsed = WorkingDiffInput.Create(input.JsonArguments);
        if (!parsed.IsSuccess)
            return Task.FromResult(Err(parsed.Error!));
        var v = parsed.Value!;

        var root = _resolver.Resolve(".");
        if (!root.IsSuccess)
            return Task.FromResult(Err(root.Error!));

        string? resolvedPath = null;
        if (v.Path is not null)
        {
            var resolved = _resolver.Resolve(v.Path);
            if (!resolved.IsSuccess)
                return Task.FromResult(Err(resolved.Error!));
            resolvedPath = resolved.Value!;
        }

        var budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
        if (!budget.IsSuccess)
            return Task.FromResult(Err(budget.Error!));

        return ToolExecution.RunAsync(input.Name, budget.Value!.Timeout, token =>
            DiffAsync(root.Value!, v.Scope, resolvedPath, v, token), ct);
    }

    private async Task<ToolResult> DiffAsync(
        string repoRoot, string scope, string? resolvedPath, WorkingDiffInput v, CancellationToken ct)
    {
        var diff = await _git.GetDiffAsync(repoRoot, scope, resolvedPath, ct);
        if (!diff.IsSuccess)
            return Err(diff.Error!);
        var o = diff.Value!;

        var target = resolvedPath ?? "none";
        if (o.Stats.Files == 0)
            return new ToolResult(
                $"[working-diff scope={v.Scope} path={target}: no differences]", false);

        var sb = new StringBuilder();
        sb.Append($"[working-diff scope={v.Scope} path={target}: {o.Stats.Files} file(s), " +
                  $"+{o.Stats.Additions}/-{o.Stats.Deletions} lines]\n");
        sb.Append(o.Patch);
        if (o.Truncated)
        {
            // Exactly one separating newline between the verbatim patch and the warning.
            if (!o.Patch.EndsWith('\n'))
                sb.Append('\n');
            sb.Append($"[warning] truncated at {PatchCharCap} chars; total {o.TotalChars} " +
                      "\u2014 narrow with path/scope");
        }
        return new ToolResult(sb.ToString(), false);
    }

    private static ToolResult Err(Error error) => new($"Error [{error.Code}]: {error.Message}", true);
}
