namespace eThangAgent.ToolDomain;

/// <summary>The external roots a session has been granted by a message's quoted path.
///     <para>A quoted path is the user naming one file, so the grant is the FILE itself: the
///     tools reach that file and nothing beside it. A directory the user quoted grants the
///     directory's contents, because that is what quoting a directory means.</para>
/// <para>Grants are read-only. A message says which file to look at, never that the file may
///     be written, so the write tools keep refusing with <c>TrustedRootReadOnly</c> and the
///     model learns the distinction from the refusal.</para>
/// <para>Mutable and process-local: the set lives as long as the session that collected it,
///     and the app carries it to the conversation host on every turn because the host is a
///     different process with its own container.</para></summary>
public sealed class SessionPathGrants
{
  private readonly Lock _gate = new();
  private readonly List<TrustedDirectory> _granted = [];

  /// <summary>Grants one quoted path, read-only. A path that is not absolute, or that cannot
  ///     be canonicalized, grants nothing: a grant is a statement about a real location, and
  ///     a relative path names no location until a workspace is chosen for it.</summary>
  public void Grant(string path)
  {
    if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
    {
      return;
    }

    string root;
    try
    {
      root = TrustedRootPath.Normalize(path);
    }
    catch (Exception ex) when (
        ex is ArgumentException or NotSupportedException or PathTooLongException)
    {
      return;
    }

    lock (_gate)
    {
      if (!_granted.Any(directory => string.Equals(directory.Root, root, StringComparison.OrdinalIgnoreCase)))
      {
        _granted.Add(new TrustedDirectory(root));
      }
    }
  }

  /// <summary>The grants collected so far, in the order they were made.</summary>
  public IReadOnlyList<TrustedDirectory> List
  {
    get
    {
      lock (_gate)
      {
        return [.. _granted];
      }
    }
  }
}
