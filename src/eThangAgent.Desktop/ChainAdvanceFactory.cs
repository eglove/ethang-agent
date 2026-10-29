using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop;

/// <summary>Builds the advance service for one session (issue #301 D1/D2).
///     <para>WHY A FACTORY AND NOT A REGISTRATION: the chain store is bound to a WORKSPACE at
///     construction and lives in that session's own container, so there is no single instance
///     to inject. The session is the key, and this is the one place it is turned into an
///     advance.</para>
///     <para>Null is a first-class answer: a container that wired no chain machinery (a
///     headless or test host) advances nothing, and the shell then behaves exactly as it did
///     before this issue.</para></summary>
internal static class ChainAdvanceFactory
{
  /// <summary>The advance service for <paramref name="session"/>, or null when its container
  ///     wired no chain machinery.</summary>
  /// <param name="session">The session whose container owns the chain store.</param>
  /// <param name="shell">Resolves the live shell, lazily - the shell constructs the sessions
  ///     it will later be asked to close and continue.</param>
  public static ChainAdvanceService? For(AgentSession session, Func<MainViewModel?> shell)
  {
    ArgumentNullException.ThrowIfNull(session);
    ArgumentNullException.ThrowIfNull(shell);
    return session.Services.GetService<ChainService>() is { } chains
      ? new ChainAdvanceService(chains, new ChainAdvanceHost(
          closeSession: rootId => shell() is { } live
              ? live.CloseSessionAsync(rootId)
              : Task.CompletedTask,
          startSession: (assignment, predecessor) => shell() is { } live
              ? live.StartChainStepSessionAsync(assignment, predecessor)
              : Task.FromResult(Result.Failure<string>(new DomainError("TabClosed",
                  "the shell is not ready; the next chain step was not started.")))))
      : null;
  }
}
