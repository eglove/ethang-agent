using eThangAgent.ToolDomain;
using eThangAgent.Windows.ACL;

namespace eThangAgent.Desktop;

/// <summary>One running process per workspace: a launch for a directory another process
///     already owns signals that process and exits, so two windows can never write one
///     workspace's history.
///     <para>THE NAME IS DERIVED FROM THE ROOT AND NOTHING ELSE, so the same directory always
///     contends with itself and two directories never contend. It is not a path: a path is
///     too long for a kernel object name and would carry separators into it.</para></summary>
internal static class WorkspaceInstanceHandoff
{
  /// <summary>How long a signalling launch waits for the owning process to accept the
  ///     connection. Short, because an owner that is running is listening.</summary>
  internal static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(3);

  /// <summary>The instance name for a workspace root.</summary>
  /// <param name="workspaceRoot">The workspace's directory path, in any spelling.</param>
  internal static string InstanceName(string workspaceRoot) =>
      SingleInstanceGate.NameFor(WorkspaceIdentity.Normalize(workspaceRoot));

  /// <summary>Tells the process that owns <paramref name="workspaceRoot"/> to raise its
  ///     window. False when no process owns it.</summary>
  /// <param name="workspaceRoot">The workspace the launch asked for.</param>
  internal static bool TrySignalExisting(string workspaceRoot) =>
      SingleInstanceGate.TrySignal(InstanceName(workspaceRoot),
          WorkspaceIdentity.Normalize(workspaceRoot), SignalTimeout);

  /// <summary>Claims <paramref name="workspaceRoot"/>, or returns null when another
  ///     process owns it. The returned gate raises <paramref name="onSignalled"/> with the
  ///     root a later launch asked for.</summary>
  /// <param name="workspaceRoot">The workspace this process is opening.</param>
  /// <param name="onSignalled">What to do when another launch signals this one.</param>
  internal static SingleInstanceGate? Acquire(string workspaceRoot, Action<string> onSignalled)
  {
    ArgumentNullException.ThrowIfNull(onSignalled);
    SingleInstanceGate? gate = SingleInstanceGate.TryAcquire(InstanceName(workspaceRoot));
    gate?.Signalled += (_, e) => onSignalled(e.Payload);
    return gate;
  }
}
