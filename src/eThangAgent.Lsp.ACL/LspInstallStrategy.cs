using System.Security.Cryptography;

namespace eThangAgent.Lsp.ACL;

/// <summary>How an install attempt ended.</summary>
public enum LspInstallOutcome
{
  /// <summary>The verified artifact is in place and resolvable.</summary>
  Installed,

  /// <summary>No bytes arrived, the arrived bytes carry no usable entry point, or the
  ///     install command failed.</summary>
  DownloadFailed,

  /// <summary>The bytes did not match the recorded checksum and were discarded.</summary>
  HashMismatch,

  /// <summary>The toolchain this row installs through is not on this machine, so nothing
  ///     was run.</summary>
  PrerequisiteMissing,
}

/// <summary>The outcome of one install attempt, with the row it concerned and the reason
///     an attempt that did not install gives. <c>Row</c> is null only for a refusal that
///     named no language the catalog serves, which is why the reason is a sentence rather
///     than a value a caller has to interpret.</summary>
public sealed record LspInstallResult(LspInstallOutcome Outcome, LspLanguageRow? Row, string? Reason = null);

/// <summary>Places one row's server on this machine inside an app-owned root and reports
///     where it landed. Every strategy is idempotent: an entry point already in place is
///     never fetched or spawned again.</summary>
public interface ILspInstallStrategy
{
  /// <summary>Installs the row's server, or reports why it could not be installed.</summary>
  Task<LspInstallResult> InstallAsync(LspLanguageRow row, string installRoot, CancellationToken cancellationToken);

  /// <summary>The installed entry point, or null when nothing is on disk.</summary>
  string? ResolveInstalled(LspLanguageRow row, string installRoot);
}

/// <summary>Where a row's server lives inside an install root. A downloaded row is keyed
///     by package and version; a toolchain row is keyed by language and version, because
///     its target directory is chosen by this app rather than by a package layout.</summary>
internal static class LspInstallLayout
{
  /// <summary>The directory a downloaded row is extracted into.</summary>
  public static string PackageDirectory(LspLanguageRow row, string installRoot) =>
      Path.Combine(installRoot, row.PackageId, row.Version);

  /// <summary>The directory a toolchain command installs into.</summary>
  public static string ToolchainPrefix(LspLanguageRow row, string installRoot) =>
      Path.Combine(installRoot, row.LanguageId, row.Version);

  /// <summary>The directory a row of any kind is placed in.</summary>
  public static string DirectoryFor(LspLanguageRow row, string installRoot) =>
      row.InstallKind == LspInstallKind.Toolchain
          ? ToolchainPrefix(row, installRoot)
          : PackageDirectory(row, installRoot);

  /// <summary>The entry point inside <paramref name="directory"/>.</summary>
  public static string EntryPoint(LspLanguageRow row, string directory) =>
      Path.Combine(
          directory,
          row.EntryPointRelativePath.Replace('/', Path.DirectorySeparatorChar),
          row.ServerFileName);

  /// <summary>Every name the entry point may carry inside <paramref name="directory"/>. A
  ///     downloaded row lands exactly the file its artifact holds. A toolchain row's launcher
  ///     is written by the toolchain, which chooses the wrapper its platform uses, so the
  ///     row's own name is tried first and the other Windows launcher wrappers after it.</summary>
  public static IReadOnlyList<string> EntryPointCandidates(LspLanguageRow row, string directory)
  {
    ArgumentNullException.ThrowIfNull(row);
    ArgumentException.ThrowIfNullOrWhiteSpace(directory);
    string declared = EntryPoint(row, directory);
    if (row.InstallKind != LspInstallKind.Toolchain)
    {
      return [declared];
    }

    string stem = Path.Combine(Path.GetDirectoryName(declared)!, Path.GetFileNameWithoutExtension(declared));
    return [.. new[] { declared, stem + ".cmd", stem + ".bat", stem + ".exe" }
        .Distinct(StringComparer.OrdinalIgnoreCase)];
  }

  /// <summary>The installed entry point, or null when it is not on disk.</summary>
  public static string? InstalledEntryPoint(LspLanguageRow row, string installRoot)
  {
    ArgumentNullException.ThrowIfNull(row);
    ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
    string directory = DirectoryFor(row, installRoot);
    return EntryPointCandidates(row, directory).FirstOrDefault(File.Exists);
  }

  /// <summary>The identity an install is remembered by. Two rows sharing a package id stay
  ///     distinct because the language is part of the key.</summary>
  public static string Key(LspLanguageRow row) =>
      row.LanguageId + "/" + row.PackageId + "/" + row.Version;
}

/// <summary>Hashes a downloaded artifact in the form its row records.</summary>
internal static class LspArtifactChecksum
{
  /// <summary>Base64 SHA-512, the form a NuGet feed publishes.</summary>
  public static string Sha512Base64(byte[] bytes) => Convert.ToBase64String(SHA512.HashData(bytes));

  /// <summary>Lowercase hex SHA-256, the form a release page publishes.</summary>
  public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
