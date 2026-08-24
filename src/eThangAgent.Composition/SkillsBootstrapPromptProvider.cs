using eThangAgent.ModelDomain;
using eThangAgent.SkillDomain;

namespace eThangAgent.Composition;

/// <summary>Session-start bootstrap injection: the verbatim using-skills skill
/// wrapped in EXTREMELY_IMPORTANT tags plus an inline tool-mapping constant, so the
/// model starts every session knowing how to find and use skills without loading
/// using-skills again. Built once per session; no caching needed.</summary>
public sealed class SkillsBootstrapPromptProvider : ISystemPromptProvider
{
    public const string SkillName = "using-skills";

    // Single injection source for harness tool binding. This constant and the
    // body of src/eThangAgent.Skill.Domain/skills/EthangToolsMapping.md MUST stay
    // word-for-word identical — change both together or neither.
    private const string ToolMapping =
        """
        # eThang Agent Tool Mapping
        
        Skills name actions; this harness binds them to real tools:
        
        | Action (as named by skills) | Binding |
        | --- | --- |
        | Read a file | `read` (optional startLine/endLine for line ranges) |
        | Write / edit files | `write` / `edit` |
        | Search files | `search_files` |
        | Run commands, tests, or git plumbing | `exec` — C# scripting through the exec engine (Roslyn); never shell scripts |
        | Dispatch a subagent | `spawn` (non-blocking, returns an id; poll `status`; fetch the report with `result`) |
        | Create/update todos | `todo` tool |
        | Invoke a skill / load its content | `skill_view` tool (never read raw skill paths; the skill store IS the mechanism) |
        | List available skills | `skill_list` tool |
        | Ask the human partner a clarifying question | `clarify` tool (MANDATORY during brainstorming) |
        | Store or read specs, plans, ledgers, briefs, reports | `state` tools — `state.get` / `state.set` / `state.append` (CAS ledger lines) / `state.list` / `state.find` (full-text search) / `state.prune` (SDD cleanup) |
        | Commit work | `git_commit` tool (never raw shell commits) |
        
        Windows-native throughout. Tests run via the dotnet CLI with xUnit (`dotnet test`);
        repo automation is plain `dotnet` CLI invocations — no `.ps1`/`.sh`/`.cmd`/`.bat`.
        
        The using-skills skill is ALREADY ACTIVE — do not load it again. Load other skills with skill_view when they apply. This bootstrap is injected once per session.
        """;

    private readonly ISkillCatalog _catalog;

    public SkillsBootstrapPromptProvider(ISkillCatalog catalog)
        => _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public string Build()
    {
        // Blocking is safe: built-in catalogs resolve synchronously from embedded
        // resources. A missing built-in here is a packaging defect — programmer
        // error, not a domain failure — so it fails fast with an exception.
        var result = _catalog.GetAsync(SkillName).GetAwaiter().GetResult();
        if (!result.IsSuccess || result.Value is null)
            throw new InvalidOperationException(
                $"Built-in skill '{SkillName}' is missing from the skill catalog " +
                "(packaging defect); cannot build the skills bootstrap prompt.");

        var skill = result.Value;
        var markdown =
            $"---\nname: {skill.Name}\ndescription: {skill.Description}\n---\n\n{skill.Body}";
        return $"<EXTREMELY_IMPORTANT>\n\n{markdown}\n\n{ToolMapping}\n\n</EXTREMELY_IMPORTANT>";
    }
}
