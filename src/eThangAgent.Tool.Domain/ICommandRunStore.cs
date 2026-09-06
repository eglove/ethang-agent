using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Persistence for completed user command runs, scoped to one workspace.
///     Ids are assigned by the store (monotonic per workspace) so resumed sessions
///     and the read-back tool resolve the same history.</summary>
public interface ICommandRunStore
{
  Task<Result<CommandRun>> AddAsync(CommandRun run, CancellationToken ct = default);

  Task<Result<CommandRun>> GetAsync(int id, CancellationToken ct = default);

  Task<Result<CommandRun>> GetLatestAsync(CancellationToken ct = default);
}
