using System.Security.Cryptography;
using System.Text;

namespace eThangAgent.Lsp.ACL;

public static class LspPipeNames
{
  public static string ForWorkspaceId(string workspaceId)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
    byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(workspaceId));
    return "ethang-lsp-" + Convert.ToHexString(hash, 0, 8);
  }
}
