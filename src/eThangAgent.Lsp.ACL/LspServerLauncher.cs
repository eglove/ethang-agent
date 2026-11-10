using System.Diagnostics;

namespace eThangAgent.Lsp.ACL;

/// <summary>Builds the launch for a resolved server. The server speaks LSP over stdio,
/// so its streams are always redirected pipes and no shell is involved. The command line
/// is the catalog row's own arguments, which is where a server's stdio flag or launcher
/// jar lives; a row with no arguments gets the bare executable.</summary>
public static class LspServerLauncher
{
  /// <summary>The stdio transport argument a server that takes one accepts.</summary>
  public const string StdioArgument = "--stdio";

  public static ProcessStartInfo BuildStartInfo(LspServerLaunch launch)
  {
    ArgumentNullException.ThrowIfNull(launch);
    ProcessStartInfo psi = new(launch.ServerPath)
    {
      CreateNoWindow = true,
      UseShellExecute = false,
      RedirectStandardInput = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
    };
    foreach (string argument in LspServerCatalog.ForLanguage(launch.LanguageId)?.Arguments ?? [])
    {
      psi.ArgumentList.Add(argument);
    }

    return psi;
  }
}
