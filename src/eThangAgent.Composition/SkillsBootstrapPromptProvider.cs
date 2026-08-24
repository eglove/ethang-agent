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

    // Single injection source for harness tool binding. Keep in sync with
    // src/eThangAgent.Skill.Domain/skills/EthangToolsMapping.md when tools change.
    private const string ToolMapping =
        """
        Tool mapping for this harness (eThang Agent): skills name actions; bind them:
        - Read a file -> read tool; write/edit files -> write/edit; search files -> search_files
        - Run shell commands/tests/git plumbing -> exec (C# scripting)
        - Dispatch a subagent -> spawn sub-agent capability
        - Create/update todos -> todo tool; invoke or list skills -> skill_view / skill_list
        - Ask the human a clarifying question -> clarify tool (MANDATORY during brainstorming)
        - Commit work -> git_commit tool once available; never raw shell commits

        The using-skills skill is ALREADY ACTIVE — do not load it again. Load other
        skills with skill_view when they apply. This bootstrap is injected once per session.
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
