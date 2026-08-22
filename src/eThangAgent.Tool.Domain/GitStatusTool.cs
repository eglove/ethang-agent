using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed class GitStatusTool : ITool
{
    private readonly WorkspacePathResolver _resolver;
    private readonly IGitQueryAccess _git;

    public ToolDefinition Definition { get; } = new(
        "git_status",
        "Show the working-tree status of the repository at the workspace root. Takes no " +
        "arguments — pass {} (or nothing at all). Output begins with an annotation line " +
        "`[git-status <branch>: S staged, U unstaged, T untracked]`; a fully clean tree " +
        "reports `[git-status <branch>: clean]` instead. Non-empty groups follow under " +
        "`staged:`, `unstaged:`, and `untracked:` headers; entries are the porcelain lines " +
        "themselves (two-char XY code, space, path). Empty groups are omitted entirely. " +
        "Errors begin with `Error [Code]:`.",
        []);

    public GitStatusTool(WorkspacePathResolver resolver, IGitQueryAccess git)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _git = git ?? throw new ArgumentNullException(nameof(git));
    }

    public async Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
        var args = ParseArguments(input.JsonArguments);
        if (!args.IsSuccess)
            return Err(args.Error!);

        var root = _resolver.Resolve(".");
        if (!root.IsSuccess)
            return Err(root.Error!);

        var status = await _git.GetStatusAsync(root.Value!, ct);
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

    /// <summary>Zero parameters: arguments must be absent or an empty JSON object.</summary>
    private static Result<bool> ParseArguments(string jsonArguments)
    {
        if (string.IsNullOrWhiteSpace(jsonArguments))
            return Result<bool>.Success(true);

        JsonElement json;
        try
        {
            using var doc = JsonDocument.Parse(jsonArguments);
            json = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return Result<bool>.Failure(new Error("InvalidJsonArguments",
                $"Arguments are not valid JSON: {ex.Message}"));
        }
        if (json.ValueKind != JsonValueKind.Object)
            return Result<bool>.Failure(new Error("InvalidJsonArguments",
                "Arguments must be a JSON object."));

        var unknown = json.EnumerateObject().Select(p => p.Name).ToList();
        if (unknown.Count > 0)
            return Result<bool>.Failure(new Error("UnknownParameter",
                $"Unknown parameter(s): {string.Join(", ", unknown)}. " +
                "This tool takes no arguments."));

        return Result<bool>.Success(true);
    }

    private static ToolResult Err(Error error) => new($"Error [{error.Code}]: {error.Message}", true);
}
