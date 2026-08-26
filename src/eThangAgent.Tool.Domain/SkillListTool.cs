using System.Text.Json;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.ToolDomain;

public sealed class SkillListTool(ISkillCatalog catalog, ILearnedSkillStore learned) : ITool
{
  private const int DescriptionLimit = 60;

  private readonly ISkillCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
  private readonly ILearnedSkillStore _learned = learned ?? throw new ArgumentNullException(nameof(learned));

  public ToolDefinition Definition { get; } = new(
      "skill_list",
      "List available methodology skills — built-ins shipped with the app plus skills " +
      "learned earlier — merged and sorted by name. Takes no parameters besides the " +
      "mandatory timeoutSeconds budget; other arguments are rejected. Output is one header line " +
      "`[skills: N available]`, then one line per skill: `<name> <builtin|learned> " +
      "v<version>  <description>` with the name padded to 20 characters and the description " +
      "truncated to 60 characters with an appended … when longer. If a source cannot be " +
      "read, its skills are omitted and a trailing line is appended — `[warning] built-in " +
      "skills unavailable: <reason>` or `[warning] learned skills unavailable: <reason>` — " +
      "while the listing itself still succeeds. Errors begin with `Error [Code]:`.",
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
      ],
      ["timeoutSeconds"]);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<bool> parsed = ParseArguments(input.JsonArguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(Err(parsed.Error!));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return !budget.IsSuccess
      ? Task.FromResult(Err(budget.Error!))
      : ToolExecution.RunAsync(input.Name, budget.Value!.Timeout, ListAsync, ct);
  }

  private async Task<ToolResult> ListAsync(CancellationToken ct)
  {
    List<string> warnings = [];
    List<SkillDefinition> skills = [];

    Result<IReadOnlyList<SkillDefinition>> builtIn = await _catalog.ListAsync(ct).ConfigureAwait(false);
    if (builtIn.IsSuccess)
    {
      skills.AddRange(builtIn.Value!);
    }
    else
    {
      warnings.Add($"[warning] built-in skills unavailable: {builtIn.Error!.Message}");
    }

    Result<IReadOnlyList<SkillDefinition>> learned = await _learned.ListAsync(ct).ConfigureAwait(false);
    if (learned.IsSuccess)
    {
      skills.AddRange(learned.Value!);
    }
    else
    {
      warnings.Add($"[warning] learned skills unavailable: {learned.Error!.Message}");
    }

    List<string> lines =
    [
      $"[skills: {skills.Count} available]",
      .. skills
          .OrderBy(s => s.Name, StringComparer.Ordinal)
          .Select(FormatRow),
      .. warnings,
    ];

    return new ToolResult(string.Join("\n", lines), false);
  }

  internal static string SourceLabel(SkillSource source) =>
      source == SkillSource.BuiltIn ? "builtin" : "learned";

  private static string FormatRow(SkillDefinition skill) =>
      $"{skill.Name,-20} {SourceLabel(skill.Source)} v{skill.Version}  {Truncate(skill.Description)}";

  private static string Truncate(string description) =>
      description.Length <= DescriptionLimit
          ? description
          : description[..DescriptionLimit] + '\u2026';

  /// <summary>skill_list carries no parameters of its own — only the mandatory
  ///     <c>timeoutSeconds</c> budget shared by every tool call.</summary>
  private static Result<bool> ParseArguments(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Fail(baseParse.Error!)
;
    }

    Result<TimeSpan> budget = ToolTimeout.Parse(baseParse.Value);
    if (!budget.IsSuccess)
    {
      return Fail(budget.Error!);
    }

    List<string> unknown = [.. baseParse.Value.EnumerateObject()
        .Select(p => p.Name)
        .Where(n => n != ToolTimeout.ParameterName)];
    return unknown.Count > 0
      ? Fail(new DomainError("UnknownParameter",
          $"Unknown parameter(s): {string.Join(", ", unknown)}. " +
          $"This tool takes no parameters besides {ToolTimeout.ParameterName}."))
      : Result.Success<bool>(true);
  }

  private static Result<bool> Fail(DomainError err) => Result.Failure<bool>(err);

  private static ToolResult Err(DomainError error) => new($"Error [{error.Code}]: {error.Message}", true);
}
