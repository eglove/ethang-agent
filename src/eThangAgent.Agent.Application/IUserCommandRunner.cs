using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Agent.Application;

/// <summary>Runs one user command (the ! chat command) against the session's
///     workspace. Implemented by <see cref="UserCommandRunner"/>; hosts and surfaces
///     depend on this seam, not the concrete runner.</summary>
public interface IUserCommandRunner
{
  Task<Result<CommandRun>> RunAsync(string command, CancellationToken ct = default);
}
