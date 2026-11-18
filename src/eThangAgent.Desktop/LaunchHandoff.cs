using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop;

/// <summary>What a launch decided to do, before any window exists.</summary>
/// <param name="IsHandedOff">Whether the workspace was handed to the process that already had
///     it open, so this launch creates no window.</param>
/// <param name="ExitCode">The process exit code the decision calls for.</param>
/// <param name="WorkspaceRoot">The normalized workspace the launch named, or null when it
///     named none.</param>
/// <param name="Message">The one line to report, or null when there is nothing to report.
///     </param>
internal sealed record LaunchHandoffResult(bool IsHandedOff, int ExitCode, string? WorkspaceRoot,
    string? Message);

/// <summary>The launch decision: a launch that names a workspace another process already owns
///     signals that process and exits, so two windows can never write one workspace's history.
///     <para>THE DECISION IS SEPARATE FROM THE WINDOW SEQUENCE. It runs before any Avalonia
///     application exists, so it is a pure function over the argument and the instance names,
///     and the program acts on its answer.</para>
///     <para>AN ARGUMENT THAT IS NOT AN EXISTING DIRECTORY IS NOT A WORKSPACE: signalling a
///     name minted from a path that is not there would report a handoff that never
///     happened.</para></summary>
internal static class LaunchHandoff
{
  /// <summary>The exit code a launch returns when it handed its workspace to the process that
  ///     already had it open. Distinct from 0 (a normal run) and 1 (a failed startup).</summary>
  internal const int HandedOffExitCode = 2;

  /// <summary>Decides what a launch for <paramref name="workspaceArgument"/> should do: hand
  ///     the workspace to the process that owns it, or start the app.</summary>
  /// <param name="workspaceArgument">The launch's workspace argument, or null when it named
  ///     none.</param>
  internal static LaunchHandoffResult Resolve(string? workspaceArgument)
      // The argument is a directory the user pointed the launch at; a path that is not there
      // has no workspace behind it, so this launch starts the app instead of signalling.
      => string.IsNullOrWhiteSpace(workspaceArgument) || !Directory.Exists(workspaceArgument)
          ? Start()
          : ResolveExisting(WorkspaceIdentity.Normalize(workspaceArgument));

  /// <summary>The decision for a normalized root that names a directory that exists.</summary>
  private static LaunchHandoffResult ResolveExisting(string root)
      => root.Length == 0 ? Start() : Signal(root);

  /// <summary>Tells the process that owns the workspace to raise its window and reports the
  ///     handoff; a workspace no process owns starts the app.</summary>
  private static LaunchHandoffResult Signal(string root)
      => WorkspaceInstanceHandoff.TrySignalExisting(root)
          ? HandedOff(root)
          : new LaunchHandoffResult(false, 0, root, null);

  private static LaunchHandoffResult HandedOff(string root) => new(true, HandedOffExitCode, root,
      $"workspace '{root}' is already open in another process; that window was raised.");

  /// <summary>An ordinary launch: the app starts and stops at the launcher.</summary>
  private static LaunchHandoffResult Start() => new(false, 0, null, null);
}
