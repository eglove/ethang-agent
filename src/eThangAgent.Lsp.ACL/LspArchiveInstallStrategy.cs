using System.IO.Compression;

namespace eThangAgent.Lsp.ACL;

/// <summary>Installs a row from a pinned release archive. The downloaded bytes are verified
///     against the row's recorded SHA-256 before anything is extracted, so a substituted
///     archive never reaches the disk. Extraction happens in a sibling temporary directory
///     and is moved into place in one step, so a half-written tree is never resolvable.</summary>
public sealed class LspArchiveInstallStrategy : ILspInstallStrategy
{
  private readonly Func<LspLanguageRow, Task<byte[]?>> _download;
  private readonly Func<string, bool> _fileExists;

  public LspArchiveInstallStrategy(
      Func<LspLanguageRow, Task<byte[]?>> download,
      Func<string, bool>? fileExists = null)
  {
    _download = download ?? throw new ArgumentNullException(nameof(download));
    _fileExists = fileExists ?? File.Exists;
  }

  /// <summary>The default strategy: the real downloader over the row's recorded address.</summary>
  public static LspArchiveInstallStrategy CreateDefault() =>
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

    if (!string.Equals(LspArtifactChecksum.Sha256Hex(bytes), row.Checksum, StringComparison.OrdinalIgnoreCase))
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

  private bool Extract(LspLanguageRow row, string installRoot, byte[] bytes)
  {
    string finalDirectory = LspInstallLayout.PackageDirectory(row, installRoot);
    string temporary = finalDirectory + ".tmp-" + Guid.NewGuid().ToString("N");
    try
    {
      Directory.CreateDirectory(temporary);
      if (!WriteEntryPoint(row, temporary, bytes))
      {
        return false;
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

  /// <summary>Writes the entry point and every file that ships beside it, so a server that
  ///     depends on its neighbours is usable. False when the archive carries no entry point
  ///     at the recorded location.</summary>
  private static bool WriteEntryPoint(LspLanguageRow row, string destination, byte[] bytes)
  {
    string target = Path.Combine(destination, row.EntryPointRelativePath);
    Directory.CreateDirectory(target);
    using MemoryStream stream = new(bytes);
    if (IsGZip(stream))
    {
      using GZipStream gzip = new(stream, CompressionMode.Decompress);
      bool found = false;
      foreach ((string name, Stream content) in LspTarReader.Read(gzip))
      {
        using (content)
        {
          found |= WriteOne(row, target, name, content);
        }
      }

      return found;
    }

    stream.Seek(0, SeekOrigin.Begin);
    using ZipArchive zip = new(stream, ZipArchiveMode.Read);
    bool written = false;
    foreach (ZipArchiveEntry entry in zip.Entries)
    {
      if (entry.FullName.EndsWith('/'))
      {
        continue;
      }

      using Stream content = entry.Open();
      written |= WriteOne(row, target, entry.FullName, content);
    }

    return written;
  }

  private static bool IsGZip(Stream archive)
  {
    if (archive.Length < 2)
    {
      return false;
    }

    int first = archive.ReadByte();
    int second = archive.ReadByte();
    archive.Seek(0, SeekOrigin.Begin);
    return first == 0x1F && second == 0x8B;
  }

  /// <summary>Copies one archive entry into the destination when it sits inside the row's
  ///     entry point directory. True when the copied entry is the server itself.</summary>
  private static bool WriteOne(LspLanguageRow row, string destination, string name, Stream content)
  {
    string prefix = row.EntryPointRelativePath.Replace('\\', '/').TrimEnd('/');
    prefix = prefix.Length == 0 ? string.Empty : prefix + "/";
    string normalized = name.Replace('\\', '/');
    if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
      return false;
    }

    string relative = normalized[prefix.Length..];
    if (relative.Length == 0 || relative.EndsWith('/'))
    {
      return false;
    }

    string file = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    using (FileStream output = File.Create(file))
    {
      content.CopyTo(output);
    }

    return string.Equals(Path.GetFileName(relative), row.ServerFileName, StringComparison.OrdinalIgnoreCase);
  }
}
