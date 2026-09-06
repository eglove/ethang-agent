using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Agent.Application;

/// <summary>Runs one user command (the ! chat command) and books its results: the
///     shell run at the workspace root through the <see cref="IShellCommandAccess"/>
///     seam, persistence of the completed run through <see cref="ICommandRunStore"/>
///     (ids are assigned by the store and are stable across restarts), and a compact
///     System message appended to the session conversation so the model learns that a
///     command ran, its id, and its exit code — the output itself stays out of the
///     context and is fetched on demand through the command_output tool. Runs are
///     independent of the LLM turn loop and may execute mid-turn.
///     </summary>
public sealed class UserCommandRunner(
    string workspaceRoot,
    IShellCommandAccess shell,
    ICommandRunStore store,
    Conversation? conversation) : IUserCommandRunner
{
  private const int DefaultTimeoutSeconds = 300;

  private readonly string _workspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot)
      ? throw new ArgumentException("Workspace root must be a non-empty path.", nameof(workspaceRoot))
      : workspaceRoot;
  private readonly IShellCommandAccess _shell = shell ?? throw new ArgumentNullException(nameof(shell));
  private readonly ICommandRunStore _store = store ?? throw new ArgumentNullException(nameof(store));
  private readonly Conversation? _conversation = conversation;

  public async Task<Result<CommandRun>> RunAsync(string command, CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(command))
    {
      return Result.Failure<CommandRun>(new DomainError("EmptyCommand",
          "Command must be a non-empty string."));
    }

    Result<ShellRun> run = await _shell.RunAsync(
        _workspaceRoot, command, TimeSpan.FromSeconds(DefaultTimeoutSeconds), ct).ConfigureAwait(false);
    if (!run.IsSuccess)
    {
      return Result.Failure<CommandRun>(run.Error);
    }

    DateTimeOffset ranAt = DateTimeOffset.UtcNow;
    Result<CommandRun> saved = await _store.AddAsync(
        new CommandRun(0, command, run.Value.ExitCode, run.Value.TimedOut, run.Value.Output, ranAt),
        ct).ConfigureAwait(false);
    if (!saved.IsSuccess)
    {
      return saved;
    }

    _conversation?.AddSystemMessage(Describe(saved.Value));
    return saved;
  }

  /// <summary>The compact model-facing line: what ran, its id, and how it ended —
  ///     success, exit code, or timeout. Points the model at command_output for the
  ///     stored output; the output itself never rides this message.</summary>
  private static string Describe(CommandRun run)
  {
    string outcome = run.TimedOut
        ? "timed out (partial output captured)"
        : $"exit code {run.ExitCode}";
    return $"User ran: `{run.Command}` (id: {run.Id}, {outcome}). " +
        "Output is stored — call command_output to read it if the user references it.";
  }
}
