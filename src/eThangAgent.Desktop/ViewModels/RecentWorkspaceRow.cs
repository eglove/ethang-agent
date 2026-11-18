namespace eThangAgent.Desktop.ViewModels;

/// <summary>One row of the launcher's recent-workspace list: the directory, the name shown for
///     it, and whether it still exists on disk.
///     <para>THE EXISTENCE PROBE HAPPENS ONCE, when the list is built, so a row's state is a
///     fact recorded at load time rather than a file-system call inside a property getter the
///     UI thread would run on every render.</para></summary>
/// <param name="Root">The normalized directory path.</param>
/// <param name="Name">The directory's own name.</param>
/// <param name="Detail">The path, or the path followed by 'not found'.</param>
/// <param name="Exists">Whether the directory was present when the list was built.</param>
internal sealed record RecentWorkspaceRow(string Root, string Name, string Detail, bool Exists)
{
  /// <summary>Builds a row from a stored binding, probing the directory once.</summary>
  /// <param name="recent">The stored root and the moment it was opened.</param>
  public static RecentWorkspaceRow From(ToolDomain.RecentWorkspace recent)
  {
    ArgumentNullException.ThrowIfNull(recent);
    string trimmed = recent.Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    string name = Path.GetFileName(trimmed);
    bool exists = Directory.Exists(recent.Root);
    return new RecentWorkspaceRow(recent.Root,
        name.Length == 0 ? recent.Root : name,
        exists ? recent.Root : recent.Root + " — not found",
        exists);
  }

  /// <summary>A missing directory is dimmed so the list reads as unavailable at a glance.
  ///     </summary>
  public double Dimming => Exists ? 1.0 : 0.45;

  /// <summary>The hover text: the full path, plus why the row cannot be opened.</summary>
  public string ToolTip => Exists ? Root : Root + " — this directory no longer exists.";
}
