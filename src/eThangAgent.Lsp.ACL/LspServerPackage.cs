namespace eThangAgent.Lsp.ACL;

/// <summary>One downloadable language server: the package that carries it, the pinned
/// version, the SHA512 of that exact package, and where the entry point sits inside
/// it. The pinned hash is the trust argument: a package that does not match it is
/// never extracted, so a compromised feed cannot substitute a binary.</summary>
public sealed record LspServerPackage(
    string LanguageId,
    string PackageId,
    string Version,
    string Sha512,
    string EntryPointRelativePath,
    string ServerFileName)
{
  /// <summary>The package download address for this pinned version.</summary>
  public Uri DownloadUrl =>
      new("https://api.nuget.org/v3-flatcontainer/" + PackageId + "/" + Version
      + "/" + PackageId + "." + Version + ".nupkg");

  public bool MatchesHash(string base64Sha512) =>
      !string.IsNullOrWhiteSpace(base64Sha512)
      && string.Equals(base64Sha512, Sha512, StringComparison.Ordinal);
}
