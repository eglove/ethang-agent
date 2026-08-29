using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed class GitCommitTool(IPathResolver resolver, IGitCommitAccess commits,
    ICommitStyleProvider styleProvider) : ITool
{
  private readonly IPathResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
  private readonly IGitCommitAccess _commits = commits ?? throw new ArgumentNullException(nameof(commits));
  private readonly ICommitStyleProvider _styleProvider = styleProvider ?? throw new ArgumentNullException(nameof(styleProvider));

  public ToolDefinition Definition { get; } = new(
      "git_commit",
      "Commit the CURRENT INDEX of the repository at the workspace root with a fully " +
      "validated message. Without 'files' this never stages — stage first, then call this. With the optional " +
      "'files' array the tool stages exactly those workspace-relative paths, then commits the index (never " +
      "anything else). timeoutSeconds and description are " +
      "mandatory: description is the " +
      "single-line subject (at most 72 characters after trimming). The commit style is a " +
      "host setting resolved at execution time — the user picks Conventional, Gitmoji, or " +
      "None, never the model — and the selected style's rules apply: Conventional requires " +
      "type from the fixed set and an optional lowercase scope; Gitmoji requires emoji_key " +
      "(an exact ':name:' catalog key) and forbids type/scope; None allows description " +
      "alone. The session bootstrap prompt carries the active style's guidance skill. " +
      "body optionally adds a paragraph after a blank line. Output begins with an " +
      "annotation line `[git-commit <hash>] committed on <branch>` followed by the " +
      "committed message exactly as committed. Validation and backend errors begin with " +
      "`Error [Code]:`.",
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("type", ToolParameterType.Text,
                "Conventional type: feat, fix, docs, style, refactor, perf, test, build, ci, chore, revert."),
            new ToolParameter("scope", ToolParameterType.Text,
                "Optional Conventional scope: lowercase letters, digits, hyphens."),
            new ToolParameter("emoji_key", ToolParameterType.Text,
                "Gitmoji style only: exact ':name:' key from the gitmoji catalog."),
            new ToolParameter("description", ToolParameterType.Text,
                "Single-line subject, at most 72 characters after trimming."),
            new ToolParameter("body", ToolParameterType.Text,
                "Optional body paragraph, appended after a blank line."),
            new ToolParameter("files", ToolParameterType.TextArray,
                "Optional JSON array of workspace-relative paths: the tool stages exactly these paths immediately before committing, and nothing else. Rules: entries must be non-empty relative paths (no drive, no leading slash, no '..' segment, no whole-tree '.'). Omit to commit the index as-is."),
      ],
      ["timeoutSeconds", "description"]);

  public async Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<GitCommitInput> parsed = GitCommitInput.Create(input.JsonArguments);
    if (!parsed.IsSuccess)
    {
      return Err(parsed.Error);
    }

    GitCommitInput v = parsed.Value;

    // The style is host state, resolved live per call: a settings change takes
    // effect on the very next commit of an open session. A failure (corrupt
    // stored value) surfaces verbatim instead of silently falling back.
    Result<CommitStyle> style = await _styleProvider.GetAsync(ct).ConfigureAwait(false);
    if (!style.IsSuccess)
    {
      return Err(style.Error);
    }

    // Message assembly first: every validation code surfaces verbatim before any
    // git work happens.
    Result<CommitMessage> message = CommitMessage.Create(
        style.Value, v.Type, v.Scope, v.EmojiKey, v.Description, v.Body);
    if (!message.IsSuccess)
    {
      return Err(message.Error);
    }

    Result<string> root = _resolver.Resolve(".");
    if (!root.IsSuccess)
    {
      return Err(root.Error);
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return !budget.IsSuccess
      ? Err(budget.Error)
      : await ToolExecution.RunAsync(input.Name, budget.Value.Timeout, token =>
          CommitAsync(root.Value, message.Value.Rendered, v.Files?.Paths, token), ct).ConfigureAwait(false);
  }

  private async Task<ToolResult> CommitAsync(string repoRoot, string message,
      IReadOnlyList<string>? files, CancellationToken ct)
  {
    if (files is not null)
    {
      Result<bool> staged = await _commits.StageAsync(repoRoot, files, ct).ConfigureAwait(false);
      if (!staged.IsSuccess)
      {
        return Err(staged.Error);
      }
    }

    Result<GitCommitOutcome> committed = await _commits.CommitAsync(repoRoot, message, ct).ConfigureAwait(false);
    if (!committed.IsSuccess)
    {
      return Err(committed.Error);
    }

    GitCommitOutcome o = committed.Value;

    return new ToolResult($"[git-commit {o.Hash}] committed on {o.Branch}\n{o.Message}", false);
  }

  private static ToolResult Err(DomainError error) => new($"Error [{error.Code}]: {error.Message}", true);
}
