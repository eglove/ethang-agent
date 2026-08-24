using eThangAgent.ModelDomain;

namespace eThangAgent.Composition;

/// <summary>Injects the workspace root and, when present, the verbatim contents of the
/// root's AGENTS.md into the system prompt, announcing the file as read. Built once at
/// startup with the user-chosen root; an absent AGENTS.md contributes nothing.</summary>
public sealed class WorkspaceInstructionsPromptProvider : ISystemPromptProvider
{
    private readonly string _root;

    public WorkspaceInstructionsPromptProvider(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Workspace root must be a non-empty path.", nameof(root));
        _root = Path.GetFullPath(root);
    }

    public string Build()
    {
        var agentsFile = Path.Combine(_root, "AGENTS.md");
        if (!File.Exists(agentsFile))
            return string.Empty;

        var contents = File.ReadAllText(agentsFile);
        return
            $"""
             Working directory: {_root}
             Its AGENTS.md has been read; verbatim contents follow.

             <agents-file source="{agentsFile}">
             {contents.TrimEnd()}
             </agents-file>
             """;
    }
}