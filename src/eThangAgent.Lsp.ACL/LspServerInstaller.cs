using System.IO.Compression;
using System.Security.Cryptography;

namespace eThangAgent.Lsp.ACL;

/// <summary>How an install attempt ended.</summary>
public enum LspInstallOutcome
{
  /// <summary>The verified package is extracted and resolvable.</summary>
  Installed,

  /// <summary>No bytes arrived, or the arrived bytes carry no usable entry point.</summary>
  DownloadFailed,

  /// <summary>The bytes did not match the pinned hash and were discarded.</summary>
  HashMismatch,
}

/// <summary>The outcome of one install attempt, with the package it concerned.</summary>
public sealed record LspInstallResult(LspInstallOutcome Outcome, LspServerPackage Package);

/// <summary>Installs a pinned language server package into an app-owned cache.
/// Bytes are verified against the pinned SHA512 before anything is extracted, so a
/// substituted package never reaches the disk. Extraction happens in a sibling
/// temporary directory and is moved into place in one step, so a half-written tree
/// is never resolvable. A failed download is remembered, which keeps an offline
/// machine from re-attempting on every resolve.</summary>
public sealed class LspServerInstaller
{
  private readonly string _root;
  private readonly Func<LspServerPackage, Task<byte[]?>> _download;
  private readonly Lock _gate = new();
  private readonly Dictionary<string, Task<LspInstallResult>> _inflight = new(StringComparer.Ordinal);
  private readonly HashSet<string> _failed = new(StringComparer.Ordinal);

  public LspServerInstaller(string root, Func<LspServerPackage, Task<byte[]?>> download)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(root);
    _root = root;
    _download = download ?? throw new ArgumentNullException(nameof(download));
  }

  /// <summary>The default cache root: the app's own local data directory.</summary>
  public static string DefaultRoot =>
      Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
          "eThangAgent",
          "language-servers");

  /// <summary>The installed entry point, or null when nothing verified is on disk.</summary>
  public string? ResolveInstalled(LspServerPackage package)
  {
    ArgumentNullException.ThrowIfNull(package);
    string candidate = EntryPointPath(package);
    return File.Exists(candidate) ? candidate : null;
  }

  /// <summary>Installs the package, or reports why it could not be installed. Concurrent
  /// callers for the same package share one download and all observe its outcome.</summary>
  public async Task<LspInstallResult> InstallAsync(LspServerPackage package, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(package);
    string key = Key(package);
    Task<LspInstallResult> attempt;
    lock (_gate)
    {
      if (_failed.Contains(key))
      {
        return new LspInstallResult(LspInstallOutcome.DownloadFailed, package);
      }
      if (!_inflight.TryGetValue(key, out Task<LspInstallResult>? running))
      {
        running = InstallCoreAsync(package, key, cancellationToken);
        _inflight[key] = running;
      }
      attempt = running;
    }
    return await attempt.ConfigureAwait(false);
  }

  private async Task<LspInstallResult> InstallCoreAsync(
      LspServerPackage package,
      string key,
      CancellationToken cancellationToken)
  {
    byte[]? bytes = await _download(package).ConfigureAwait(false);
    cancellationToken.ThrowIfCancellationRequested();
    if (bytes is null)
    {
      Remember(key);
      return new LspInstallResult(LspInstallOutcome.DownloadFailed, package);
    }
    string actual = Convert.ToBase64String(SHA512.HashData(bytes));
    if (!package.MatchesHash(actual))
    {
      return new LspInstallResult(LspInstallOutcome.HashMismatch, package);
    }
    if (!Extract(package, bytes))
    {
      Remember(key);
      return new LspInstallResult(LspInstallOutcome.DownloadFailed, package);
    }
    return new LspInstallResult(LspInstallOutcome.Installed, package);
  }

  private void Remember(string key)
  {
    lock (_gate)
    {
      _failed.Add(key);
    }
  }

  /// <summary>Writes the entry point and every file beside it into a temporary sibling
  /// directory and moves it into place, so the final directory only ever appears
  /// complete. A server that ships dependencies is unusable without them.</summary>
  private bool Extract(LspServerPackage package, byte[] bytes)
  {
    string finalDirectory = PackageDirectory(package);
    if (File.Exists(EntryPointPath(package)))
    {
      return true;
    }
    string temporary = finalDirectory + ".tmp-" + Guid.NewGuid().ToString("N");
    try
    {
      using MemoryStream stream = new(bytes);
      using ZipArchive archive = new(stream, ZipArchiveMode.Read);
      string prefix = package.EntryPointRelativePath.Replace('\\', '/').TrimEnd('/') + "/";
      List<ZipArchiveEntry> shipped = [.. archive.Entries
          .Where(e => e.FullName.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
              && !e.FullName.EndsWith('/'))];
      if (!shipped.Any(e => string.Equals(
              Path.GetFileName(e.FullName.Replace('\\', '/')),
              package.ServerFileName,
              StringComparison.OrdinalIgnoreCase)))
      {
        return false;
      }
      foreach (ZipArchiveEntry entry in shipped)
      {
        string relative = entry.FullName.Replace('\\', '/')[prefix.Length..];
        string target = Path.Combine(temporary, package.EntryPointRelativePath, relative.Replace('/', Path.DirectorySeparatorChar));
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
      return File.Exists(EntryPointPath(package));
    }
    finally
    {
      if (Directory.Exists(temporary))
      {
        Directory.Delete(temporary, recursive: true);
      }
    }
  }

  private string PackageDirectory(LspServerPackage package) =>
      Path.Combine(_root, package.PackageId, package.Version);

  private string EntryPointPath(LspServerPackage package) =>
      Path.Combine(PackageDirectory(package), package.EntryPointRelativePath, package.ServerFileName);

  private static string Key(LspServerPackage package) => package.PackageId + "/" + package.Version;
}
