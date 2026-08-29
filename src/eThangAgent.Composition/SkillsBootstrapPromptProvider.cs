using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition;

/// <summary>Session-start bootstrap injection: the verbatim using-skills skill plus the
/// verbatim ethang-tools-mapping skill body — BOTH read from the skill catalog so the
/// embedded markdown is the single source of harness tool binding (no mirrored constant
/// to drift) — plus the selected commit style's guidance skill (the user's host
/// setting; Conventional when no provider is wired) — all wrapped in
/// EXTREMELY_IMPORTANT tags with the already-active notice. Built once per
/// session; no caching needed.</summary>
public sealed class SkillsBootstrapPromptProvider(ISkillCatalog catalog,
    ICommitStyleProvider? styleProvider = null) : ISkillCatalogDependentSystemPromptProvider
{
  public const string SkillName = "using-skills";
  public const string MappingSkillName = "ethang-tools-mapping";

  /// <summary>Name prefix of the built-in commit-style guidance skills; the selected
  ///     style's name completes it.</summary>
  public const string CommitStyleSkillPrefix = "commit-style-";

  /// <summary>Commit-style skill injected when no provider is wired (hosts without a
  ///     preference store) — the documented default.</summary>
  public const string DefaultCommitStyleSkillName = CommitStyleSkillPrefix + "conventional";

  private const string AlreadyActiveNotice =
      "The using-skills skill is ALREADY ACTIVE — do not load it again. Load other " +
      "skills with skill_view when they apply. This bootstrap is injected once per session.";

  private readonly ISkillCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

  /// <summary>The style provider is optional: hosts without a preference store (tests,
  ///     minimal embeddings) bootstrap with the Conventional default skill.</summary>
  private readonly ICommitStyleProvider? _styleProvider = styleProvider;

  public string Build()
  {
    // Blocking is safe: built-in catalogs resolve synchronously from embedded
    // resources. A missing built-in here is a packaging defect — programmer
    // error, not a domain failure — so it fails fast with an exception.
    SkillDefinition skills = Require(SkillName);
    SkillDefinition mapping = Require(MappingSkillName);
    SkillDefinition commitStyle = Require(CommitStyleSkillName());

    string skillsMarkdown =
        $"---\nname: {skills.Name}\ndescription: {skills.Description}\n---\n\n{skills.Body}";
    return $"<EXTREMELY_IMPORTANT>\n\n{skillsMarkdown}\n\n{mapping.Body}\n\nActive commit style guidance (the user's host setting; the git_commit tool " +
        $"enforces it):\n\n{commitStyle.Body}\n\n{AlreadyActiveNotice}\n\n</EXTREMELY_IMPORTANT>";
  }

  /// <summary>Resolves which commit-style guidance skill to inject: the wired
  ///     provider's live choice, or the Conventional default when no provider is
  ///     wired. Read per Build() call so a session built after a settings change
  ///     picks up the new style.</summary>
  private string CommitStyleSkillName()
  {
    if (_styleProvider is null)
    {
      return DefaultCommitStyleSkillName;
    }

    // Blocking is safe for the same reason as Require: the built-in skills this
    // pairs with are embedded resources. A corrupt stored style value is host
    // state, not a packaging defect — it degrades to the default rather than
    // crashing session construction; the git_commit tool still surfaces the
    // typed error on the next commit.
    Result<CommitStyle> style = _styleProvider.GetAsync().GetAwaiter().GetResult();
    return !style.IsSuccess
      ? DefaultCommitStyleSkillName
      : CommitStyleSkillPrefix + SuffixOf(style.Value);
  }

  /// <summary>The skill-file suffix for each style; skill names are lowercase by
  ///     convention (CA1308 forbids the ToLower shortcut, and explicit mapping
  ///     documents the pairing).</summary>
  private static string SuffixOf(CommitStyle style) => style switch
  {
    CommitStyle.Conventional => "conventional",
    CommitStyle.Gitmoji => "gitmoji",
    CommitStyle.None => "none",
    _ => "conventional",
  };

  private SkillDefinition Require(string name)
  {
    Result<SkillDefinition> result = _catalog.GetAsync(name).GetAwaiter().GetResult();
    return !result.IsSuccess
      ? throw new InvalidOperationException(
          $"Built-in skill '{name}' is missing from the skill catalog " +
          "(packaging defect); cannot build the skills bootstrap prompt.")
      : result.Value;
  }
}

public interface ISkillCatalogDependentSystemPromptProvider : ISystemPromptProvider
{
}
