namespace eThangAgent.Lsp.ACL;

/// <summary>file:// URI mapping (issue #353), owned by the ACL.</summary>
public static class LspUriMapper
{
  public static string ToUri(string path)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    string full = Path.GetFullPath(path);
    return new Uri(full).AbsoluteUri;
  }

  public static string FromUri(string uri)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(uri);
    string local = new Uri(uri).LocalPath;

    // A server may report a Windows path in the authority-less form
    // 'file:///c%3A/dir/file.cs', whose LocalPath is '/c:/dir/file.cs'. The leading
    // separator is not part of the path on this platform.
    if (local.Length >= 3 && local[0] == '/' && char.IsAsciiLetter(local[1]) && local[2] == ':')
    {
      return Path.GetFullPath(char.ToUpperInvariant(local[1]) + local[2..]);
    }

    return local;
  }
}
