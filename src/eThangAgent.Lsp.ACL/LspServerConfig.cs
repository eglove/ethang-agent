namespace eThangAgent.Lsp.ACL;

/// <summary>Per-language server configuration (issue #353): command, arguments,
/// claimed file extensions. The executable must already exist; installation is
/// out of scope.</summary>
public sealed record LspServerConfig(string LanguageId, string Command, IReadOnlyList<string> Arguments, IReadOnlyList<string> Extensions)
{
  public bool ClaimsExtension(string extension)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(extension);
    string ext = extension.StartsWith('.') ? extension : "." + extension;
    return Extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
  }
}
