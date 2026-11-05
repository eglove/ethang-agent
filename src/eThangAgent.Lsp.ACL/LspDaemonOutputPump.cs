namespace eThangAgent.Lsp.ACL;

/// <summary>Reads the daemon's stdout to the end, feeding every line to a
///     <see cref="LspServerStatusMonitor"/> on the way.
///     <para>THE STREAM MUST BE READ REGARDLESS: a redirected stdout nobody drains fills its
///     pipe buffer and blocks the daemon mid-write, so the drain is not optional. What was
///     missing was that the lines were discarded - the daemon announces its install work
///     there and nothing else could observe it.</para>
///     <para>The pump ends when the daemon exits or closes its stdout, which is also when the
///     stream can no longer say anything.</para></summary>
public static class LspDaemonOutputPump
{
  /// <summary>Drains <paramref name="output"/> line by line until it ends, announcing each
  ///     line to <paramref name="monitor"/>. Lines that are not announcements change nothing.
  ///     </summary>
  public static async Task DrainAsync(TextReader output, LspServerStatusMonitor monitor,
      CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(monitor);
    try
    {
      while (await output.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
      {
        _ = monitor.Announce(line);
      }
    }
    // Named decision (CA1031): the daemon's stdout ends when its process does, and a
    // read that fails on a dying pipe is the same fact - the stream is over, not an error
    // the caller can act on.
    catch (Exception ex) when (ex is IOException or ObjectDisposedException
        or OperationCanceledException)
    {
      // The stream is finished; nothing is left to announce.
    }
  }
}
