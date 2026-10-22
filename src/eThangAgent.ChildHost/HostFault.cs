namespace eThangAgent.ChildHost;

/// <summary>Formats a host-side fault for the wire. A wrapper exception says only
///     that something threw, so the reader is left guessing ("The type initializer for
///     'Microsoft.Data.Sqlite.SqliteConnection' threw an exception" - the message that
///     hid a missing native library behind a whole session of failed turns,
///     2026-10-22). The rendering carries BOTH halves: the CONTEXT (the outer
///     message - what the host was doing) and the ROOT CAUSE (the innermost
///     exception's type and message - the fact someone can act on).</summary>
internal static class HostFault
{
  /// <summary>An exception rendered for a reader: the context first, then the root
  ///     cause. The wrapper alone is not enough, and the cause alone loses the
  ///     sentence that turns a DLL error into a fix ("the deployment is incomplete:
  ///     the host's dependency manifest must sit next to its executable").</summary>
  public static string Describe(Exception error)
  {
    ArgumentNullException.ThrowIfNull(error);

    Exception root = error;
    while (root.InnerException is { } inner)
    {
      root = inner;
    }

    return ReferenceEquals(root, error)
        ? error.Message
        : error.Message + " (" + root.GetType().Name + ": " + root.Message + ")";
  }
}
