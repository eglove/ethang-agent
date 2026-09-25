using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.Agent.Application;

/// <summary>One resolved skill invocation (spec #28 task 1): the verbatim
/// System line hosts append to the conversation, plus the size-guard
/// verdict. SkipReason is non-null only when the body was too large to
/// inline.</summary>
public sealed record SkillInvocation(
    string Name,
    string Description,
    string Body,
    string SystemLine,
    bool BodyInlined,
    string? SkipReason);

/// <summary>The shared user-invocation core (spec #28): resolves a skill
/// name against the composite catalog (case-insensitive; ambiguity is a
/// typed error listing the matches), renders ONE System message with the
/// body inline, and applies the 4,000-character size guard (oversize falls
/// back to a skill_view pointer). Manual skills resolve identically — the
/// manual flag governs the listing, never this channel. No skill_usage rows
/// are written here (the view-hygiene rule from spec #19).</summary>
public sealed partial class SkillInvocationService(ISkillCatalog catalog)
{
  private const int InlineBodyLimit = 4000;
  private const int DescriptionLimit = 60;

  private readonly ISkillCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));


  public async Task<Result<SkillInvocation>> InvokeAsync(string name, string? arguments, CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(name))
    {
      return Result.Failure<SkillInvocation>(new DomainError("InvalidInput", "Skill name must be a non-empty string."));
    }

    Result<IReadOnlyList<SkillDefinition>> listed = await _catalog.ListAsync(ct).ConfigureAwait(false);
    if (!listed.IsSuccess)
    {
      return Result.Failure<SkillInvocation>(listed.Error);
    }

    List<SkillDefinition> matches = [.. listed.Value
        .Where(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))];

    return matches.Count switch
    {
      0 => Result.Failure<SkillInvocation>(new DomainError("SkillNotFound",
          $"No skill named '{name.Trim()}' is in the catalog.")),
      1 => Result.Success(Render(matches[0], arguments)),
      _ => Result.Failure<SkillInvocation>(new DomainError("AmbiguousSkill",
          $"'{name.Trim()}' matches multiple skills: {string.Join(", ", matches.Select(m => m.Name))}. Name one exactly.")),
    };
  }

  private static SkillInvocation Render(SkillDefinition skill, string? arguments)
  {
    string description = skill.Description.Length <= DescriptionLimit
        ? skill.Description
        : skill.Description[..DescriptionLimit] + "…";
    string header = $"[skill invoked: {skill.Name}]\n{description}";
    string argsLine = string.IsNullOrWhiteSpace(arguments)
        ? string.Empty
        : $"\narguments: {arguments.Trim()}";

    if (skill.Body.Length > InlineBodyLimit)
    {
      string line = $"{header}{argsLine}\n[body too large to inline ({skill.Body.Length} characters) — load it with skill_view]";
      return new SkillInvocation(skill.Name, description, skill.Body, line, BodyInlined: false, SkipReason: "oversize");
    }

    return new SkillInvocation(skill.Name, description, skill.Body, $"{header}\n{skill.Body}{argsLine}", BodyInlined: true, SkipReason: null);
  }
}