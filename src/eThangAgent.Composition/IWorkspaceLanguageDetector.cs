using eThangAgent.Lsp.ACL;

namespace eThangAgent.Composition;

/// <summary>Which of the catalog's languages a workspace holds. The desktop reads detection
///     through this seam so no view-model type names a language-server type of its own.</summary>
public interface IWorkspaceLanguageDetector
{
  /// <summary>Every language the workspace holds, ordered by manifest presence, then by file
  ///     count, then by language id. A root that does not exist holds nothing.</summary>
  IReadOnlyList<DetectedLanguage> Detect(string workspaceRoot);
}

/// <summary>Detection over the app's own workspace walk.</summary>
public sealed class WorkspaceLanguageDetector : IWorkspaceLanguageDetector
{
  /// <summary>The shared instance: detection holds no state between calls.</summary>
  public static WorkspaceLanguageDetector Default { get; } = new();

  public IReadOnlyList<DetectedLanguage> Detect(string workspaceRoot) =>
      LspWorkspaceLanguages.Detect(workspaceRoot);
}
