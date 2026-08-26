using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed class GitCommitTool(IPathResolver resolver, IGitCommitAccess commits) : ITool
{
  private readonly IPathResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
  private readonly IGitCommitAccess _commits = commits ?? throw new ArgumentNullException(nameof(commits));

  public ToolDefinition Definition { get; } = new(
      "git_commit",
      "Commit the CURRENT INDEX of the repository at the workspace root with a fully " +
      "validated message. Never stages anything — stage first, then call this. timeoutSeconds and style are " +
      "mandatory: style is exactly 'Conventional', 'Gitmoji', or 'None' (case-sensitive); description is the " +
      "single-line subject (at most 72 characters after trimming). Conventional requires " +
      "type from the fixed set and an optional lowercase scope; Gitmoji requires emoji_key " +
      "(an exact ':name:' catalog key) and forbids type/scope; None allows description " +
      "alone. body optionally adds a paragraph after a blank line. Output begins with an " +
      "annotation line `[git-commit <hash>] committed on <branch>` followed by the " +
      "committed message exactly as committed. Validation and backend errors begin with " +
      "`Error [Code]:`.",
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("style", ToolParameterType.Text,
                "Exactly 'Conventional', 'Gitmoji', or 'None' (case-sensitive)."),
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
      ],
      ["timeoutSeconds", "style", "description"]);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<GitCommitInput> parsed = GitCommitInput.Create(input.JsonArguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(Err(parsed.Error!));
    }

    GitCommitInput v = parsed.Value!;

    // Message assembly first: every validation code surfaces verbatim before any
    // git work happens.
    Result<CommitMessage> message = CommitMessage.Create(
        v.Style, v.Type, v.Scope, v.EmojiKey, v.Description, v.Body);
    if (!message.IsSuccess)
    {
      return Task.FromResult(Err(message.Error!));
    }

    Result<string> root = _resolver.Resolve(".");
    if (!root.IsSuccess)
    {
      return Task.FromResult(Err(root.Error!));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return !budget.IsSuccess
      ? Task.FromResult(Err(budget.Error!))
      : ToolExecution.RunAsync(input.Name, budget.Value!.Timeout, token =>
        CommitAsync(root.Value!, message.Value!.Rendered, token), ct);
  }

  private async Task<ToolResult> CommitAsync(string repoRoot, string message, CancellationToken ct)
  {
    Result<GitCommitOutcome> committed = await _commits.CommitAsync(repoRoot, message, ct).ConfigureAwait(false);
    if (!committed.IsSuccess)
    {
      return Err(committed.Error!);
    }

    GitCommitOutcome o = committed.Value!;

    return new ToolResult($"[git-commit {o.Hash}] committed on {o.Branch}\n{o.Message}", false);
  }

  private static ToolResult Err(DomainError error) => new($"Error [{error.Code}]: {error.Message}", true);
}
