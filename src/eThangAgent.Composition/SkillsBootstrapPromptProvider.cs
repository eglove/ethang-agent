using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.Composition;

/// <summary>Session-start bootstrap injection: the verbatim using-skills skill plus the
/// verbatim ethang-tools-mapping skill body — BOTH read from the skill catalog so the
/// embedded markdown is the single source of harness tool binding (no mirrored constant
/// to drift) — wrapped in EXTREMELY_IMPORTANT tags with the already-active notice. Built
/// once per session; no caching needed.</summary>
public sealed class SkillsBootstrapPromptProvider(ISkillCatalog catalog) : ISkillCatalogDependentSystemPromptProvider
{
  public const string SkillName = "using-skills";
  public const string MappingSkillName = "ethang-tools-mapping";

  private const string AlreadyActiveNotice =
      "The using-skills skill is ALREADY ACTIVE — do not load it again. Load other " +
      "skills with skill_view when they apply. This bootstrap is injected once per session.";

  private readonly ISkillCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

  public string Build()
  {
    // Blocking is safe: built-in catalogs resolve synchronously from embedded
    // resources. A missing built-in here is a packaging defect — programmer
    // error, not a domain failure — so it fails fast with an exception.
    SkillDefinition skills = Require(SkillName);
    SkillDefinition mapping = Require(MappingSkillName);

    string skillsMarkdown =
        $"---\nname: {skills.Name}\ndescription: {skills.Description}\n---\n\n{skills.Body}";
    return $"<EXTREMELY_IMPORTANT>\n\n{skillsMarkdown}\n\n{mapping.Body}\n\n{AlreadyActiveNotice}\n\n</EXTREMELY_IMPORTANT>";
  }

  private SkillDefinition Require(string name)
  {
    Result<SkillDefinition> result = _catalog.GetAsync(name).GetAwaiter().GetResult();
    return !result.IsSuccess || result.Value is null
      ? throw new InvalidOperationException(
          $"Built-in skill '{name}' is missing from the skill catalog " +
          "(packaging defect); cannot build the skills bootstrap prompt.")
      : result.Value;
  }
}

public interface ISkillCatalogDependentSystemPromptProvider : ISystemPromptProvider
{
}
