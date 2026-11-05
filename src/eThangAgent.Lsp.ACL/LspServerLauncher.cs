using System.Diagnostics;

namespace eThangAgent.Lsp.ACL;

/// <summary>Builds the launch for a resolved server. The server speaks LSP over stdio,
/// so its streams are always redirected pipes and no shell is involved.</summary>
public static class LspServerLauncher
{
  /// <summary>The stdio transport argument every supported server accepts.</summary>
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
    psi.ArgumentList.Add(StdioArgument);
    return psi;
  }
}
