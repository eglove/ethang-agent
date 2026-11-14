using eThangAgent.FileSystem.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.OrphanHost;

/// <summary>The writer half of the orphan sweep's crash test: persists one image through the
///     REAL <see cref="SessionImageStore"/> into a named session folder, prints the path it
///     wrote, and - for the write-only verb - exits immediately with nothing naming that file.
///     </summary>
/// <remarks>A process that dies between the file write and the transcript flush is the case this
///     program reproduces. It writes through production code rather than manufacturing a file, so
///     the orphan the test sweeps is the one this codebase would actually leave behind.</remarks>
internal static class Program
{
  private static int Main(string[] args)
  {
    if (args is not [var verb, var baseDirectory, var sessionId, var sizeText]
        || !int.TryParse(sizeText, System.Globalization.CultureInfo.InvariantCulture, out int size))
    {
      Console.Error.WriteLine("usage: <verb> <baseDirectory> <sessionId> <pngBytes>");
      return 2;
    }

    if (!string.Equals(verb, "write-only", StringComparison.Ordinal))
    {
      Console.Error.WriteLine($"unknown verb '{verb}'");
      return 2;
    }

    byte[] payload = new byte[size];
    new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(payload, 0);
    SessionImageStore store = new(sessionId, baseDirectory);
    Result<StoredImage> stored = store.PersistAsync("image/png", payload).GetAwaiter().GetResult();
    if (!stored.IsSuccess)
    {
      Console.Error.WriteLine($"write failed: [{stored.Error?.Code}] {stored.Error?.Message}");
      return 1;
    }

    // The path goes to stdout so the parent knows exactly what the dead process left behind,
    // and nothing else is written: no message, no row, no reference. That absence IS the orphan.
    Console.Out.WriteLine(stored.Value.Path);
    Console.Out.Flush();
    return 0;
  }
}
