namespace eThangAgent.Lsp.ACL;

/// <summary>Fetches a pinned package's bytes over HTTPS. A transport failure is reported
/// as absent bytes, which the installer records as a failed download.</summary>
public static class LspServerDownloader
{
  private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(10) };

  public static async Task<byte[]?> DownloadAsync(LspServerPackage package, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(package);
#pragma warning disable CA1031 // Named decision: an offline machine is a missing server, not a crash.
    try
    {
      return await Client.GetByteArrayAsync(package.DownloadUrl, cancellationToken).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
      return null;
    }
#pragma warning restore CA1031
  }
}
