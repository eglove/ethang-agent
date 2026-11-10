namespace eThangAgent.Lsp.ACL;

/// <summary>Fetches a row's pinned artifact over HTTPS. A transport failure is reported as
///     absent bytes, which the installer records as a failed download.</summary>
public static class LspServerDownloader
{
  private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(10) };

  /// <summary>Where a row's artifact is published: the NuGet flat container for a package,
  ///     the recorded release address for an archive.</summary>
  public static Uri Address(LspLanguageRow row)
  {
    ArgumentNullException.ThrowIfNull(row);
    if (row.InstallKind == LspInstallKind.Archive)
    {
      return string.IsNullOrWhiteSpace(row.ArchiveAddress)
          ? throw new ArgumentException("An archive row needs a recorded address.", nameof(row))
          : new Uri(row.ArchiveAddress);
    }

#pragma warning disable S1075 // Named decision: the NuGet flat container is the recorded source of a package row.
    return new Uri("https://api.nuget.org/v3-flatcontainer/" + row.PackageId + "/" + row.Version
        + "/" + row.PackageId + "." + row.Version + ".nupkg");
#pragma warning restore S1075
  }

  public static async Task<byte[]?> DownloadAsync(LspLanguageRow row, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(row);
#pragma warning disable CA1031 // Named decision: an offline machine is a missing server, not a crash.
    try
    {
      return await Client.GetByteArrayAsync(Address(row), cancellationToken).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
      return null;
    }
#pragma warning restore CA1031
  }
}
