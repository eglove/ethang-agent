using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed class GitCommitTool : ITool
{
    private readonly IPathResolver _resolver;
    private readonly IGitCommitAccess _commits;

    public ToolDefinition Definition { get; } = new(
        "git_commit",
        "Commit the CURRENT INDEX of the repository at the workspace root with a fully " +
        "validated message. Never stages anything — stage first, then call this. style is " +
        "exactly 'Conventional', 'Gitmoji', or 'None' (case-sensitive); description is the " +
        "single-line subject (at most 72 characters after trimming). Conventional requires " +
        "type from the fixed set and an optional lowercase scope; Gitmoji requires emoji_key " +
        "(an exact ':name:' catalog key) and forbids type/scope; None allows description " +
        "alone. body optionally adds a paragraph after a blank line. Output begins with an " +
        "annotation line `[git-commit <hash>] committed on <branch>` followed by the " +
        "committed message exactly as committed. Validation and backend errors begin with " +
        "`Error [Code]:`.",
        [
            new ToolParameter("style", ToolParameterType.String,
                "Exactly 'Conventional', 'Gitmoji', or 'None' (case-sensitive)."),
            new ToolParameter("type", ToolParameterType.String,
                "Conventional type: feat, fix, docs, style, refactor, perf, test, build, ci, chore, revert."),
            new ToolParameter("scope", ToolParameterType.String,
                "Optional Conventional scope: lowercase letters, digits, hyphens."),
            new ToolParameter("emoji_key", ToolParameterType.String,
                "Gitmoji style only: exact ':name:' key from the gitmoji catalog."),
            new ToolParameter("description", ToolParameterType.String,
                "Single-line subject, at most 72 characters after trimming."),
            new ToolParameter("body", ToolParameterType.String,
                "Optional body paragraph, appended after a blank line."),
        ]);

    public GitCommitTool(IPathResolver resolver, IGitCommitAccess commits)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _commits = commits ?? throw new ArgumentNullException(nameof(commits));
    }

    public async Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
        var parsed = GitCommitInput.Create(input.JsonArguments);
        if (!parsed.IsSuccess)
            return Err(parsed.Error!);
        var v = parsed.Value!;

        // Message assembly first: every validation code surfaces verbatim before any
        // git work happens.
        var message = CommitMessage.Create(
            v.Style, v.Type, v.Scope, v.EmojiKey, v.Description, v.Body);
        if (!message.IsSuccess)
            return Err(message.Error!);

        var root = _resolver.Resolve(".");
        if (!root.IsSuccess)
            return Err(root.Error!);

        var committed = await _commits.CommitAsync(root.Value!, message.Value!.Rendered, ct);
        if (!committed.IsSuccess)
            return Err(committed.Error!);
        var o = committed.Value!;

        return new ToolResult($"[git-commit {o.Hash}] committed on {o.Branch}\n{o.Message}", false);
    }

    private static ToolResult Err(Error error) => new($"Error [{error.Code}]: {error.Message}", true);
}
