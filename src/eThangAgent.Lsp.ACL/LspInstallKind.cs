namespace eThangAgent.Lsp.ACL;

/// <summary>How a language server reaches this machine.</summary>
public enum LspInstallKind
{
  /// <summary>A pinned NuGet package, downloaded and extracted into the app cache.</summary>
  NuGet,

  /// <summary>A pinned release archive, downloaded and extracted into the app cache.</summary>
  Archive,

  /// <summary>A command run by a toolchain the machine must already provide.</summary>
  Toolchain,
}
