namespace eThangAgent.Desktop;

/// <summary>Outcome of the workspace startup flow: either a chosen root directory or a
/// request to exit the application (user declined to pick a workspace).</summary>
public sealed record WorkspaceStartupResult(bool ExitRequested, string? Root);

/// <summary>Workspace-selection decision loop: prompt until a directory is chosen or the
/// user declines. Pure control flow — the Avalonia folder picker and the required-choice
/// dialog are injected as delegates, keeping the loop unit-testable without a UI.</summary>
public sealed class WorkspaceStartupFlow
{
    /// <summary>Picks repeatedly; each cancelled pick is followed by the required dialog.
    /// Choosing "choose again" re-prompts, "exit" ends the flow with ExitRequested.</summary>
    public async Task<WorkspaceStartupResult> RunAsync(
        Func<Task<string?>> pickFolder,
        Func<Task<bool>> showRequiredDialog,
        CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var picked = await pickFolder();
            if (!string.IsNullOrWhiteSpace(picked))
                return new WorkspaceStartupResult(ExitRequested: false, Root: picked);

            if (!await showRequiredDialog())
                return new WorkspaceStartupResult(ExitRequested: true, Root: null);
        }
    }
}