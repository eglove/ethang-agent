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
    return new Uri(uri).LocalPath;
  }
}
