using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop;

/// <summary>The shell's half of a chain resume (issue #303's exit): it starts a fresh session for
///     a step a human unblocked.
///     <para>WHY THE SHELL AND NOT THE HOST: a chain step's session is a TAB CELL - a view-model,
///     a container, and a host process - and the app is the only place a tab exists. The host
///     runs the turn; the app owns the surface the turn appears in.</para>
///     <para>EVERY ACT IS BEST EFFORT AND REPORTED, never thrown: a resume can be asked for from
///     an event-pump thread and from a model's own turn, where an escaping exception has no
///     handler. The refusal is the honest answer and the chain record is the durable one.</para>
/// </summary>
/// <param name="shell">Resolves the live shell, lazily.</param>
internal sealed class ChainResumeLaunch(Func<MainViewModel?> shell) : IChainResumeLaunchPort
{
  /// <inheritdoc />
  public async Task<Result<string>> StartResumedSessionAsync(ChainStepAssignment assignment,
      CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(assignment);
    _ = ct;
    if (shell() is not { } live)
    {
      return Result.Failure<string>(new DomainError("TabClosed",
          "the shell is not ready; the resumed chain step was not started."));
    }

    // Named decision (CA1031): the launcher reports its own typed failures; a thrown fault is
    // converted here so the resume releases the claim instead of leaking it.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      return await live.StartResumedChainStepSessionAsync(assignment).ConfigureAwait(false);
    }
    catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
    {
      return Result.Failure<string>(new DomainError("SessionLaunchFailed",
          "the resumed chain step's session could not be started: " + ex.Message));
    }
  }
}
