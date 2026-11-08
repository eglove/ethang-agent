using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.ToolDomain;

/// <summary>The deterministic description linter as a read-only tool: no store,
/// no model, no I/O — one catalog lookup and the documented trigger rules.</summary>
public sealed class SkillLintTool(ISkillCatalog catalog) : ITool
{
  private readonly ISkillCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

  public ToolDefinition Definition { get; } = new(
      "skill_lint",
      "Check one skill's description against the documented trigger rules — third person; " +
      "what it does AND when to use it; front-loaded key terms. timeoutSeconds and name are " +
      "mandatory: name is the exact skill name as listed by skill_list. Read-only. " +
      "Output: `[skill-lint] '<name>' clean`, or a `[skill-lint] '<name>' has <N> finding(s):` " +
      "header followed by one `[lint] <rule>: <message>` line per finding. Errors begin with " +
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
        LintAsync(parsed.Value.Name, token), ct);
  }

  private async Task<ToolResult> LintAsync(string name, CancellationToken ct)
  {
    Result<SkillDefinition> resolved = await _catalog.GetAsync(name, ct).ConfigureAwait(false);
    return resolved.IsSuccess
        ? LintResult(name, resolved.Value.Description)
        : Err(resolved.Error);
  }

  private static ToolResult LintResult(string name, string description)
  {
    IReadOnlyList<SkillDescriptionFinding> findings = SkillDescriptionLinter.Lint(description);
    if (findings.Count == 0)
    {
      return new ToolResult($"[skill-lint] '{name}' clean", false);
    }

    string body = string.Join("\n", findings.Select(f => $"[lint] {f.Rule}: {f.Message}"));
    return new ToolResult($"[skill-lint] '{name}' has {findings.Count} finding(s):\n{body}", false);
  }

  private static ToolResult Err(DomainError error) => new($"Error [{error.Code}]: {error.Message}", true);
}
