namespace eThangAgent.Lsp.ACL;

/// <summary>A package an install places beside a language server in the same prefix, for a
///     server that resolves another package at run time rather than bundling it.</summary>
public sealed record LspCompanionPackage(string PackageId, string Version)
{
  /// <summary>The package spec a package manager installs: <c>id@version</c>.</summary>
  public string Spec => PackageId + "@" + Version;
}

/// <summary>One language this app can serve: the files it claims, the manifests that mark a
///     workspace as needing it, and everything an install needs to place a server on this
///     machine.
///     <para><c>Checksum</c> is the hash of the exact downloaded artifact, in the form its
///     source publishes: base64 SHA-512 of the <c>.nupkg</c> for a <see cref="LspInstallKind.NuGet"/>
///     row, lowercase hex SHA-256 of the archive for a <see cref="LspInstallKind.Archive"/> row,
///     and null for a <see cref="LspInstallKind.Toolchain"/> row, whose artifact the toolchain
///     resolves rather than this app.</para>
///     <para><c>ArchiveAddress</c> is where an <see cref="LspInstallKind.Archive"/> row's artifact
///     is published, and null for every other kind.</para>
///     <para><c>EntryPointRelativePath</c> is the directory inside an extracted archive that
///     holds the server, and is empty for a toolchain row, which extracts nothing.</para>
///     <para><c>CompanionPackages</c> are the packages an install places beside the server in
///     the same prefix, for a server that resolves another package at run time rather than
///     bundling it.</para></summary>
public sealed record LspLanguageRow(
    string LanguageId,
    string DisplayName,
    LspInstallKind InstallKind,
    string? PrerequisiteExecutable,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> ManifestNames,
    string PackageId,
    string Version,
    string? Checksum,
    string EntryPointRelativePath,
    string ServerFileName,
    IReadOnlyList<string> Arguments,
    string? ArchiveAddress = null,
    IReadOnlyList<LspCompanionPackage>? CompanionPackages = null)
{
  /// <summary>The packages an install places beside the server, empty when the server needs
  ///     none.</summary>
  public IReadOnlyList<LspCompanionPackage> Companions => CompanionPackages ?? [];

  /// <summary>True when <paramref name="path"/> carries an extension this row claims.</summary>
  public bool Claims(string path)
  {
    ArgumentNullException.ThrowIfNull(path);
    string extension = Path.GetExtension(path);
    if (extension.Length == 0)
    {
      return false;
    }

    foreach (string claimed in Extensions)
    {
      if (string.Equals(claimed, extension, StringComparison.OrdinalIgnoreCase))
      {
        return true;
      }
    }

    return false;
  }

  /// <summary>True when the file at <paramref name="path"/> is one of this row's
  ///     manifests, matched by whole file name or by extension: a manifest is either a fixed
  ///     file name (<c>Cargo.toml</c>) or a project-file extension (<c>.csproj</c>).</summary>
  public bool HasManifestName(string path)
  {
    ArgumentNullException.ThrowIfNull(path);
    string fileName = Path.GetFileName(path);
    string extension = Path.GetExtension(path);
    foreach (string name in ManifestNames)
    {
      if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)
          || (extension.Length > 0 && string.Equals(name, extension, StringComparison.OrdinalIgnoreCase)))
      {
        return true;
      }
    }

    return false;
  }
}
