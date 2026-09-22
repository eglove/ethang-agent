namespace eThangAgent.ComputerUse.Host;

/// <summary>The modal surface kind (spec 7): window vs attached dialog vs open/save
///     panel. Wire values are lowercase snake.</summary>
public enum SurfaceKind
{
  Window,
  Dialog,
  OpenPanel,
  SavePanel,
}

/// <summary>The surface lifecycle across captures (fix round I3b): replaced when the
///     window handle changed under the same app key, closed when the previous handle is
///     gone, stable otherwise. Wire values are lowercase snake.</summary>
public enum SurfaceLifecycle
{
  Stable,
  Replaced,
  Closed,
}

/// <summary>Class-name heuristics for surface kinds (fix round I3b): fake-testable by
///     design - the classifier maps class names and titles; the live window-class
///     provider is injected at composition (task 18 wires the real GetClassName read;
///     the mapping is NOT deferred).</summary>
public static class SurfaceClassifier
{
  private const string DialogClassName = "#32770";

  public static SurfaceKind KindFor(string windowClassName, string? title = null)
  {
    ArgumentNullException.ThrowIfNull(windowClassName);
    return IsDialogOrPanel(windowClassName, title)
      ? PanelOrDialog(title)
      : SurfaceKind.Window;
  }

  /// <summary>The dialog-class match or a panel-titled window (either routes through
  ///     the panel/dialog classifier).</summary>
  private static bool IsDialogOrPanel(string windowClassName, string? title) =>
    windowClassName.Equals(DialogClassName, StringComparison.OrdinalIgnoreCase)
    || windowClassName.Contains("dialog", StringComparison.OrdinalIgnoreCase)
    || (title is not null && IsPanelTitle(title));


  private static SurfaceKind PanelOrDialog(string? title) => title switch
  {
    null => SurfaceKind.Dialog,
    _ when title.Contains("save", StringComparison.OrdinalIgnoreCase) => SurfaceKind.SavePanel,
    _ when title.Contains("open", StringComparison.OrdinalIgnoreCase) => SurfaceKind.OpenPanel,
    _ => SurfaceKind.Dialog,
  };

  private static bool IsPanelTitle(string title) =>
    title.Contains("open", StringComparison.OrdinalIgnoreCase)
    || title.Contains("save", StringComparison.OrdinalIgnoreCase);
};

/// <summary>The lifecycle tracker (fix round I3b): compares window handles across
///     captures per app key. Replaced = new handle under the same app key; Stable = same
///     handle. Fully fake-testable: handles are just longs. The window-handle provider
///     injection happens at composition; the comparison logic here is unit-covered.</summary>
public sealed class SurfaceTracker
{
  private readonly Dictionary<string, (long Handle, SurfaceKind Kind)> _last = new(StringComparer.Ordinal);

  /// <summary>Records one observation and returns the lifecycle it represents.</summary>
  public SurfaceLifecycle Observe(string appKey, long windowHandle, SurfaceKind kind)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
    if (_last.TryGetValue(appKey, out (long Handle, SurfaceKind Kind) previous))
    {
      if (windowHandle == 0)
      {
        // R3 (fix round): the tracked handle disappeared - the lifecycle IS Closed and
        // the key is forgotten so a later observe is a fresh Stable (the Forget contract).
        _ = Forget(appKey);
        return SurfaceLifecycle.Closed;
      }

      _last[appKey] = (windowHandle, kind);
      return previous.Handle == windowHandle ? SurfaceLifecycle.Stable : SurfaceLifecycle.Replaced;
    }

    _last[appKey] = (windowHandle, kind);
    return SurfaceLifecycle.Stable; // first observation of an app key
  }

  /// <summary>Clears the recorded handle for an app key (used when it was Closed): a
  ///     later observe is then a fresh Stable.</summary>
  public bool Forget(string appKey) => _last.Remove(appKey);
};
