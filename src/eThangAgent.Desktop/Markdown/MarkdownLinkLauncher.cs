using System.ComponentModel;
using System.Diagnostics;

namespace eThangAgent.Desktop.Markdown;

/// <summary>Seam for opening a rendered markdown link with the OS default
///     handler. Only http(s) URLs ever reach the OS: the scheme gate sits here
///     (not in the click handler) so every entry point inherits it. Tests swap
///     <see cref="Override"/> to observe launches without a browser.</summary>
internal static class MarkdownLinkLauncher
{
  internal static Func<string, bool>? Override { get; set; }

  public static bool TryOpen(string url)
  {
    ArgumentNullException.ThrowIfNull(url);
    if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) ||
        parsed.Scheme is not ("http" or "https"))
    {
      return false;
    }

    if (Override is not null)
    {
      return Override(url);
    }

    try
    {
      using Process p = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })!;
      return true;
    }
    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
    {
      return false;
    }
  }
}
