using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed class GitStatusTool : ITool
{
    private readonly IPathResolver _resolver;
    private readonly IGitQueryAccess _git;

    public ToolDefinition Definition { get; } = new(
        "git_status",
        "Show the working-tree status of the repository at the workspace root. Takes no " +
        "arguments besides timeoutSeconds — pass {\"timeoutSeconds\": N} (or nothing at all). " +
        "Output begins with an annotation line " +
        "`[git-status <branch>: S staged, U unstaged, T untracked]`; a fully clean tree " +
        "reports `[git-status <branch>: clean]` instead. Non-empty groups follow under " +
        "`staged:`, `unstaged:`, and `untracked:` headers; entries are the porcelain lines " +
        "themselves (two-char XY code, space, path). Empty groups are omitted entirely. " +
        "Errors begin with `Error [Code]:`.",
        [
            new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.Integer, ToolTimeout.ParameterDescription, Minimum: 1),
        ],
        ["timeoutSeconds"]);

    public GitStatusTool(IPathResolver resolver, IGitQueryAccess git)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _git = git ?? throw new ArgumentNullException(nameof(git));
    }

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
        var args = ParseArguments(input.JsonArguments);
        if (!args.IsSuccess)
            return Task.FromResult(Err(args.Error!));

        var root = _resolver.Resolve(".");
        if (!root.IsSuccess)
            return Task.FromResult(Err(root.Error!));

        var budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
        if (!budget.IsSuccess)
            return Task.FromResult(Err(budget.Error!));

        return ToolExecution.RunAsync(input.Name, budget.Value!.Timeout, token =>
            StatusAsync(root.Value!, token), ct);
    }

    private async Task<ToolResult> StatusAsync(string repoRoot, CancellationToken ct)
    {
        var status = await _git.GetStatusAsync(repoRoot, ct);
        if (!status.IsSuccess)
            return Err(status.Error!);
        var s = status.Value!;

        var lines = new List<string>();
        var clean = s.Staged.Count == 0 && s.Unstaged.Count == 0 && s.Untracked.Count == 0;
        lines.Add(clean
            ? $"[git-status {s.Branch}: clean]"
            : $"[git-status {s.Branch}: {s.Staged.Count} staged, {s.Unstaged.Count} unstaged, " +
              $"{s.Untracked.Count} untracked]");

        // Entry lines reproduce the porcelain line verbatim: two-char code, one
        // separator space, path. Untracked paths carry no code in the domain model,
        // so the ?? marker is restored here.
        if (s.Staged.Count > 0)
        {
            lines.Add("staged:");
            lines.AddRange(s.Staged.Select(e => $"{e.Code} {e.Path}"));
        }
        if (s.Unstaged.Count > 0)
        {
            lines.Add("unstaged:");
            lines.AddRange(s.Unstaged.Select(e => $"{e.Code} {e.Path}"));
        }
        if (s.Untracked.Count > 0)
        {
            lines.Add("untracked:");
            lines.AddRange(s.Untracked.Select(p => $"?? {p}"));
        }

        return new ToolResult(string.Join("\n", lines), false);
    }

    /// <summary>git_status carries no parameters of its own — only the mandatory
    ///     <c>timeoutSeconds</c> budget shared by every tool call.</summary>
    private static Result<bool> ParseArguments(string jsonArguments)
    {
        var baseParse = ToolArguments.ParseObject(jsonArguments);
        if (!baseParse.IsSuccess)
            return Result<bool>.Failure(baseParse.Error!);
        ;

        var budget = ToolTimeout.Parse(baseParse.Value);
        if (!budget.IsSuccess)
            return Result<bool>.Failure(budget.Error!);

        var unknown = baseParse.Value.EnumerateObject()
            .Select(p => p.Name)
            .Where(n => n != ToolTimeout.ParameterName)
            .ToList();
        if (unknown.Count > 0)
            return Result<bool>.Failure(new Error("UnknownParameter",
                $"Unknown parameter(s): {string.Join(", ", unknown)}. " +
                $"This tool takes no arguments besides {ToolTimeout.ParameterName}."));

        return Result<bool>.Success(true);
    }

    private static ToolResult Err(Error error) => new($"Error [{error.Code}]: {error.Message}", true);
}
