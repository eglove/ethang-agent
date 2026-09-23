using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.ToolDomain;

public sealed class SkillViewTool(ISkillCatalog catalog, ILearnedSkillStore learned) : ITool
{
  private readonly ISkillCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
  private readonly ILearnedSkillStore _learned = learned ?? throw new ArgumentNullException(nameof(learned));

  public ToolDefinition Definition { get; } = new(
      "skill_view",
      "Show the full body of one methodology skill by name. timeoutSeconds and name are mandatory: " +
      "name is the exact " +
      "skill name as listed by skill_list. Built-ins are resolved first, then file skills, " +
      "then learned skills. Output is an annotation line `[skill <name> | " +
      "<builtin|file|learned> | v<version>]` — for file skills extended with the origin " +
      "directory: `[skill <name> | file | v<version> | <origin>]` — followed by the skill " +
      "body byte-for-byte. Learned-skill views record a usage row best-effort; built-in " +
      "and file views record nothing. If recording fails, a final line `[warning] usage " +
      "not recorded` is appended and the view still succeeds. Errors begin with " +
      "`Error [Code]:` — including `Error [SkillNotFound]:` when no skill has that name.",
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("name", ToolParameterType.Text,
                "Exact skill name from skill_list."),
      ],
      ["timeoutSeconds", "name"]);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<SkillViewInput> parsed = SkillViewInput.Create(input.JsonArguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(Err(parsed.Error));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return !budget.IsSuccess
      ? Task.FromResult(Err(budget.Error))
      : ToolExecution.RunAsync(input.Name, budget.Value.Timeout, token =>
        ViewAsync(parsed.Value.Name, token), ct);
  }

  private async Task<ToolResult> ViewAsync(string name, CancellationToken ct)
  {
    // The composite catalog is authoritative across built-ins and file skills
    // (built-ins first); a miss falls through to the learned store.
    Result<SkillDefinition> catalogHit = await _catalog.GetAsync(name, ct).ConfigureAwait(false);
    if (catalogHit.IsSuccess)
    {
      SkillDefinition skill = catalogHit.Value;
      return new ToolResult(Annotation(skill) + "\n" + skill.Body, false);
    }

    Result<SkillDefinition?> learnedResult = await _learned.GetAsync(name, ct).ConfigureAwait(false);
    if (!learnedResult.IsSuccess)
    {
      return Err(learnedResult.Error);
    }

    if (learnedResult.ValueOrNull is null)
    {
      return Err(new DomainError("SkillNotFound",
          $"No skill named '{name}'. Use skill_list to see available skills."));
    }

    // Usage recording is learned-only analytics: a failure degrades to a
    // warning, never to a failed view.
    Result<int> usage = await _learned.AppendUsageAsync(name, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);

    SkillDefinition learnedSkill = learnedResult.Value;
    string content = Annotation(learnedSkill) + "\n" + learnedSkill.Body;
    if (!usage.IsSuccess)
    {
      content += "\n[warning] usage not recorded";
    }

    return new ToolResult(content, false);
  }

  /// <summary>File skills carry their origin directory in the annotation;
  /// built-in and learned skills use the plain three-field form.</summary>
  private static string Annotation(SkillDefinition skill) =>
      skill.Source == SkillSource.File
          ? $"[skill {skill.Name} | file | v{skill.Version} | {skill.Origin}]"
          : $"[skill {skill.Name} | {SkillListTool.SourceLabel(skill.Source)} | v{skill.Version}]";

  private static ToolResult Err(DomainError error) => new($"Error [{error.Code}]: {error.Message}", true);
}
