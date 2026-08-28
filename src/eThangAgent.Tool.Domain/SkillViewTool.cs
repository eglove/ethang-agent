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
      "skill name as listed by skill_list. Built-ins are resolved first, then learned skills. " +
      "Output is an annotation line `[skill <name> | <builtin|learned> | v<version>]` followed " +
      "by the skill body byte-for-byte. Each view records a usage row best-effort; if " +
      "recording fails, a final line `[warning] usage not recorded` is appended and the view " +
      "still succeeds. Errors begin with `Error [Code]:` — including `Error [SkillNotFound]:` " +
      "when no skill has that name.",
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
      return Task.FromResult(Err(parsed.Error!));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return !budget.IsSuccess
      ? Task.FromResult(Err(budget.Error!))
      : ToolExecution.RunAsync(input.Name, budget.Value!.Timeout, token =>
        ViewAsync(parsed.Value!.Name, token), ct);
  }

  private async Task<ToolResult> ViewAsync(string name, CancellationToken ct)
  {
    // Built-ins are authoritative; names cannot collide, so any catalog
    // miss or failure safely falls through to the learned store.
    Result<SkillDefinition> builtIn = await _catalog.GetAsync(name, ct).ConfigureAwait(false);
    if (builtIn.IsSuccess)
    {
      return await RenderAsync(builtIn.Value!, ct).ConfigureAwait(false);
    }

    Result<SkillDefinition?> learnedResult = await _learned.GetAsync(name, ct).ConfigureAwait(false);
    return !learnedResult.IsSuccess
      ? Err(learnedResult.Error!)
      : learnedResult.Value is null
      ? Err(new DomainError("SkillNotFound",
          $"No skill named '{name}'. Use skill_list to see available skills."))
      : await RenderAsync(learnedResult.Value, ct).ConfigureAwait(false);
  }

  private async Task<ToolResult> RenderAsync(SkillDefinition skill, CancellationToken ct)
  {
    // Usage recording is analytics only: a failure degrades to a warning,
    // never to a failed view.
    Result<int> usage = await _learned.AppendUsageAsync(skill.Name, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);

    string content =
        $"[skill {skill.Name} | {SkillListTool.SourceLabel(skill.Source)} | v{skill.Version}]\n" +
        skill.Body;
    if (!usage.IsSuccess)
    {
      content += "\n[warning] usage not recorded";
    }

    return new ToolResult(content, false);
  }

  private static ToolResult Err(DomainError error) => new($"Error [{error.Code}]: {error.Message}", true);
}
