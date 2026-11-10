using System.IO.Compression;

namespace eThangAgent.Lsp.ACL;

/// <summary>Installs a row from a pinned NuGet package. The downloaded bytes are verified
///     against the row's recorded base64 SHA-512 before anything is extracted, so a
///     substituted package never reaches the disk. Extraction happens in a sibling temporary
///     directory and is moved into place in one step, so a half-written tree is never
///     resolvable.</summary>
public sealed class LspNuGetInstallStrategy : ILspInstallStrategy
{
  private readonly Func<LspLanguageRow, Task<byte[]?>> _download;
  private readonly Func<string, bool> _fileExists;

  public LspNuGetInstallStrategy(
      Func<LspLanguageRow, Task<byte[]?>> download,
      Func<string, bool>? fileExists = null)
  {
    _download = download ?? throw new ArgumentNullException(nameof(download));
    _fileExists = fileExists ?? File.Exists;
  }

  /// <summary>The default strategy: the real downloader over the row's recorded address.</summary>
  public static LspNuGetInstallStrategy CreateDefault() =>
      new(row => LspServerDownloader.DownloadAsync(row, CancellationToken.None));

  public async Task<LspInstallResult> InstallAsync(
      LspLanguageRow row,
      string installRoot,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(row);
    ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
    if (ResolveInstalled(row, installRoot) is not null)
    {
      return new LspInstallResult(LspInstallOutcome.Installed, row);
    }

    byte[]? bytes = await _download(row).ConfigureAwait(false);
    cancellationToken.ThrowIfCancellationRequested();
    if (bytes is null)
    {
      return new LspInstallResult(LspInstallOutcome.DownloadFailed, row);
    }

    if (!string.Equals(LspArtifactChecksum.Sha512Base64(bytes), row.Checksum, StringComparison.Ordinal))
    {
      return new LspInstallResult(LspInstallOutcome.HashMismatch, row);
    }

    LspInstallOutcome outcome = Extract(row, installRoot, bytes)
        ? LspInstallOutcome.Installed
        : LspInstallOutcome.DownloadFailed;
    return new LspInstallResult(outcome, row);
  }

  public string? ResolveInstalled(LspLanguageRow row, string installRoot) =>
      LspInstallLayout.InstalledEntryPoint(row, installRoot);

  /// <summary>Writes the entry point and every file beside it into a temporary sibling
  ///     directory and moves it into place, so the final directory only ever appears
  ///     complete. A server that ships dependencies is unusable without them.</summary>
  private bool Extract(LspLanguageRow row, string installRoot, byte[] bytes)
  {
    string finalDirectory = LspInstallLayout.PackageDirectory(row, installRoot);
    string temporary = finalDirectory + ".tmp-" + Guid.NewGuid().ToString("N");
    try
    {
      using MemoryStream stream = new(bytes);
      using ZipArchive archive = new(stream, ZipArchiveMode.Read);
      string prefix = row.EntryPointRelativePath.Replace('\\', '/').TrimEnd('/') + "/";
      List<ZipArchiveEntry> shipped = [.. archive.Entries
          .Where(e => e.FullName.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
              && !e.FullName.EndsWith('/'))];
      if (!shipped.Any(e => string.Equals(
              Path.GetFileName(e.FullName.Replace('\\', '/')),
              row.ServerFileName,
              StringComparison.OrdinalIgnoreCase)))
      {
        return false;
      }

      foreach (ZipArchiveEntry entry in shipped)
      {
        string relative = entry.FullName.Replace('\\', '/')[prefix.Length..];
        string target = Path.Combine(temporary, row.EntryPointRelativePath, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using Stream source = entry.Open();
        using FileStream destination = File.Create(target);
        source.CopyTo(destination);
      }

      Directory.CreateDirectory(Path.GetDirectoryName(finalDirectory)!);
      Directory.Move(temporary, finalDirectory);
      return true;
    }
    catch (IOException)
    {
      return _fileExists(LspInstallLayout.EntryPoint(row, finalDirectory));
    }
    finally
    {
      if (Directory.Exists(temporary))
      {
        Directory.Delete(temporary, recursive: true);
      }
    }
  }
}
