using System.Globalization;
using System.Text;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed class WorktreeTool(IPathResolver resolver, IGitWorktreeAccess worktrees) : ITool, IWorkspaceScopedTool
{
  private readonly IPathResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
  private readonly IGitWorktreeAccess _worktrees = worktrees ?? throw new ArgumentNullException(nameof(worktrees));

  /// <inheritdoc />
  public ITool RootedAt(string workspaceRoot)
      => new WorktreeTool(new WorkspacePathResolver(workspaceRoot), _worktrees);

  public ToolDefinition Definition { get; } = new(
      "worktree",
      "Manage git worktrees of the repository at the workspace root. " +
      "timeoutSeconds is mandatory; action is exactly one of 'list' (case-sensitive), 'create', or 'remove'. " +
      "name is required for create and remove (lowercase letters, digits, hyphens; ^[a-z0-9-]+$, 1..64 chars); " +
      "force (boolean, remove only) discards a dirty worktree. Output: list begins with the header line " +
      "`[worktree] name | path | branch | sha | flags` followed by one line per worktree " +
      "`name | relative-path | branch | shortsha | flags` where the flags cell is empty or holds " +
      "`[main]` for the main worktree and `[dirty]` for one with uncommitted changes, separated by single spaces; " +
      "create reports `[worktree] created <name> at <full path>`; remove reports `[worktree] removed <name>`. " +
      "Errors begin with `Error [Code]:`.",
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
          new ToolParameter("action", ToolParameterType.Text, "Exactly 'list', 'create', or 'remove' (case-sensitive)."),
          new ToolParameter("name", ToolParameterType.Text, "Worktree name for create/remove: 1..64 characters of lowercase letters, digits, and hyphens (^[a-z0-9-]+$)."),
          new ToolParameter("force", ToolParameterType.Flag, "Optional, remove only: discard uncommitted changes in the worktree being removed."),
      ],
      ["timeoutSeconds", "action"]);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<WorktreeToolInput> parsed = WorktreeToolInput.Create(input.JsonArguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(Err(parsed.Error));
    }

    WorktreeToolInput v = parsed.Value;

    Result<string> root = _resolver.Resolve(".");
    if (!root.IsSuccess)
    {
      return Task.FromResult(Err(root.Error));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return !budget.IsSuccess
      ? Task.FromResult(Err(budget.Error))
      : ToolExecution.RunAsync(input.Name, budget.Value.Timeout, token => RunAsync(root.Value, v, token), ct);
  }

  private async Task<ToolResult> RunAsync(string repoRoot, WorktreeToolInput v, CancellationToken ct)
  {
    switch (v.Action)
    {
      case WorktreeAction.List:
        Result<IReadOnlyList<WorktreeInfo>> listed = await _worktrees.ListAsync(repoRoot, ct).ConfigureAwait(false);
        return listed.IsSuccess ? RenderList(repoRoot, listed.Value) : Err(listed.Error);

      case WorktreeAction.Create:
        if (string.IsNullOrWhiteSpace(v.Name))
        {
          return InvalidName(v.Name);
        }

        Result<WorktreeName> name = WorktreeName.Create(v.Name);
        return !name.IsSuccess
          ? Err(name.Error)
          : await CreateAsync(repoRoot, name.Value.Value, ct).ConfigureAwait(false);

      case WorktreeAction.Remove:
        if (string.IsNullOrWhiteSpace(v.Name))
        {
          return InvalidName(v.Name);
        }

        Result<WorktreeName> worktreeName = WorktreeName.Create(v.Name);
        return !worktreeName.IsSuccess
          ? Err(worktreeName.Error)
          : await RemoveAsync(repoRoot, worktreeName.Value.Value, v.Force ?? false, ct).ConfigureAwait(false);

      default:
        throw new InvalidOperationException($"unreachable: input parser admits only list/create/remove, got {v.Action}");
    }
  }

  private async Task<ToolResult> CreateAsync(string repoRoot, string name, CancellationToken ct)
  {
    Result<WorktreeInfo> created = await _worktrees.CreateAsync(repoRoot, name, ct).ConfigureAwait(false);
    return created.IsSuccess
      ? new ToolResult($"[worktree] created {name} at {created.Value.Path}", false)
      : Err(created.Error);
  }

  private async Task<ToolResult> RemoveAsync(string repoRoot, string name, bool force, CancellationToken ct)
  {
    Result<bool> removed = await _worktrees.RemoveAsync(repoRoot, name, force, ct).ConfigureAwait(false);
    return removed.IsSuccess
      ? new ToolResult($"[worktree] removed {name}", false)
      : Err(removed.Error);
  }

  private static ToolResult RenderList(string repoRoot, IReadOnlyList<WorktreeInfo> worktrees)
  {
    StringBuilder sb = new();
    _ = sb.Append("[worktree] name | path | branch | sha | flags");
    foreach (WorktreeInfo w in worktrees)
    {
      _ = sb.Append('\n')
          .Append(CultureInfo.InvariantCulture, $"{w.Name} | {Relative(repoRoot, w.Path)} | {w.Branch} | {w.HeadShortSha} | {Flags(w)}");
    }

    return new ToolResult(sb.ToString(), false);
  }

  private static string Flags(WorktreeInfo w)
  {
    StringBuilder sb = new();
    if (w.IsMain)
    {
      _ = sb.Append("[main]");
    }

    if (w.IsDirty)
    {
      if (sb.Length > 0)
      {
        _ = sb.Append(' ');
      }

      _ = sb.Append("[dirty]");
    }

    return sb.ToString();
  }

  private static string Relative(string repoRoot, string path)
  {
    try
    {
      return Path.GetRelativePath(repoRoot, path);
    }
    catch (Exception ex) when (ex is ArgumentException or ArgumentNullException)
    {
      return path;
    }
  }

  private static ToolResult InvalidName(string? name) => new(
      $"Error [InvalidName]: '{name}' is not a valid worktree name; use 1..{WorktreeName.MaxLength} characters of " +
      "lowercase letters, digits, and hyphens (^[a-z0-9-]+$), with no trimming.", true);

  private static ToolResult Err(DomainError error) => new($"Error [{error.Code}]: {error.Message}", true);
}
