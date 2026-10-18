using System.Text.Json;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.ToolDomain;

public sealed class SkillListTool(ISkillCatalog catalog, ILearnedSkillStore learned) : ITool
{
  // Keep in sync: format parity with SkillsListingPromptProvider truncation
  // (SkillListingBudget.DescriptionLimit in eThangAgent.Composition) - Tool.Domain
  // cannot reference Composition, so the constant is duplicated deliberately.
  private const int DescriptionLimit = 60;

  private readonly ISkillCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
  private readonly ILearnedSkillStore _learned = learned ?? throw new ArgumentNullException(nameof(learned));

  public ToolDefinition Definition { get; } = new(
      "skill_list",
      "List available methodology skills — built-ins shipped with the app, skills loaded " +
      "from configured skill directories, and skills learned earlier — merged and sorted " +
      "by name. Takes no parameters besides the mandatory timeoutSeconds budget; other " +
      "arguments are rejected. Output is one header line `[skills: N available]`, then one " +
      "line per skill: `<name> <builtin|file|learned> v<version>[ [manual]]  <description>` — " +
      "the name padded to 20 characters, the source label `builtin` for built-ins, `file` " +
      "for file skills, and `learned` for learned skills, the literal ` [manual]` marker " +
      "between the version and the description separator only for manual-invocation-only " +
      "skills, and the description truncated to 60 characters with an appended … when " +
      "longer. After the skill rows the catalog's diagnostics render: each collision line " +
      "verbatim with its `[collision] ` prefix, every other diagnostic as `[warning] " +
      "<text>`. Source-failure warnings — `[warning] built-in skills unavailable: <reason>` " +
      "or `[warning] learned skills unavailable: <reason>` — keep their positions after the " +
      "diagnostics: an unreadable source's skills are omitted while the listing itself " +
      "still succeeds. A learned skill whose name already exists in the catalog " +
      "does not render twice: the learned row is skipped and a collision line " +
      "`[collision] <name> (learned) shadowed by <builtin|file>` is appended after the " +
      "catalog diagnostics. Errors begin with `Error [Code]:`.",
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
      return Task.FromResult(Err(parsed.Error));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return !budget.IsSuccess
      ? Task.FromResult(Err(budget.Error))
      : ToolExecution.RunAsync(input.Name, budget.Value.Timeout, ListAsync, ct);
  }

  private async Task<ToolResult> ListAsync(CancellationToken ct)
  {
    List<string> warnings = [];
    List<SkillDefinition> skills = [];

    Result<IReadOnlyList<SkillDefinition>> builtIn = await _catalog.ListAsync(ct).ConfigureAwait(false);
    AddBuiltIn(skills, warnings, builtIn);

    List<string> dedupCollisions = [];
    await MergeLearnedAsync(skills, dedupCollisions, warnings, builtIn, ct).ConfigureAwait(false);

    List<string> diagnostics = await GetDiagnosticsAsync(ct).ConfigureAwait(false);

    List<string> lines =
    [
      $"[skills: {skills.Count} available]",
      .. skills
          .OrderBy(s => s.Name, StringComparer.Ordinal)
          .Select(FormatRow),
      .. diagnostics,
      .. dedupCollisions,
      .. warnings,
    ];

    return new ToolResult(string.Join("\n", lines), false);
  }

  private static void AddBuiltIn(List<SkillDefinition> skills, List<string> warnings,
      Result<IReadOnlyList<SkillDefinition>> builtIn)
  {
    if (builtIn.IsSuccess)
    {
      skills.AddRange(builtIn.Value);
    }
    else
    {
      warnings.Add($"[warning] built-in skills unavailable: {builtIn.Error.Message}");
    }
  }

  /// <summary>Merges the learned source into <paramref name="skills"/>: deduped
  ///     against the catalog's names when the catalog is up, all rows rendered when
  ///     it is down, and a source-failure warning on the warnings list.</summary>
  private async Task MergeLearnedAsync(List<SkillDefinition> skills, List<string> dedupCollisions,
      List<string> warnings, Result<IReadOnlyList<SkillDefinition>> builtIn, CancellationToken ct)
  {
    Result<IReadOnlyList<SkillDefinition>> learnedResult = await _learned.ListAsync(ct).ConfigureAwait(false);
    if (!learnedResult.IsSuccess)
    {
      warnings.Add($"[warning] learned skills unavailable: {learnedResult.Error.Message}");
      return;
    }

    if (builtIn.IsSuccess)
    {
      MergeLearnedWithDedup(skills, dedupCollisions, learnedResult.Value, builtIn.Value);
    }
    else
    {
      // Dedup needs the catalog's names; with the catalog down the learned
      // rows all render and nothing is announced.
      skills.AddRange(learnedResult.Value);
    }
  }

  /// <summary>Presentation-layer dedup (spec #19 decision 2): a learned skill whose
  ///     name already exists in the catalog is skipped here and announced —
  ///     the composite catalog never sees learned rows, so this is the only
  ///     site where the two sources can collide.</summary>
  private static void MergeLearnedWithDedup(List<SkillDefinition> skills, List<string> dedupCollisions,
      IReadOnlyList<SkillDefinition> learnedSkills, IReadOnlyList<SkillDefinition> builtInSkills)
  {
    Dictionary<string, string> winnerLabelByName = builtInSkills
        .GroupBy(s => s.Name, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => SourceLabel(g.First().Source), StringComparer.Ordinal);
    foreach (SkillDefinition learnedSkill in learnedSkills)
    {
      if (winnerLabelByName.TryGetValue(learnedSkill.Name, out string? winnerLabel))
      {
        dedupCollisions.Add($"[collision] {learnedSkill.Name} (learned) shadowed by {winnerLabel}");
      }
      else
      {
        skills.Add(learnedSkill);
      }
    }
  }

  private async Task<List<string>> GetDiagnosticsAsync(CancellationToken ct)
  {
    // Diagnostics are an optional catalog capability: the composite skill
    // catalog carries them (collision + directory lines), bare catalogs do not.
    List<string> diagnostics = [];
    if (_catalog is ISkillCatalogDiagnostics withDiagnostics)
    {
      Result<IReadOnlyList<string>> reported =
          await withDiagnostics.GetDiagnosticsAsync(ct).ConfigureAwait(false);
      if (reported.IsSuccess)
      {
        diagnostics.AddRange(reported.Value.Select(RenderDiagnostic));
      }
    }

    return diagnostics;
  }

  internal static string SourceLabel(SkillSource source) => source switch
  {
    SkillSource.BuiltIn => "builtin",
    SkillSource.File => "file",
    SkillSource.Learned => "learned",
    // Unnamed enum values cannot occur.
    _ => "learned",
  };

  /// <summary>Collision lines already carry their '[collision] ' prefix and
  /// render verbatim; every other diagnostic is plain text that gains the
  /// '[warning] ' prefix here.</summary>
  private static string RenderDiagnostic(string line) =>
      line.StartsWith("[collision] ", StringComparison.Ordinal) ? line : "[warning] " + line;

  private static string FormatRow(SkillDefinition skill) =>
      $"{skill.Name,-20} {SourceLabel(skill.Source)} v{skill.Version}{ManualMarker(skill)}  {Truncate(skill.Description)}";

  private static string ManualMarker(SkillDefinition skill) => skill.Manual ? " [manual]" : string.Empty;

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
      return Fail(baseParse.Error);
    }

    Result<TimeSpan> budget = ToolTimeout.Parse(baseParse.Value);
    if (!budget.IsSuccess)
    {
      return Fail(budget.Error);
    }

    List<string> unknown = [.. baseParse.Value.EnumerateObject()
        .Select(p => p.Name)
        .Where(n => n != ToolTimeout.ParameterName)];
    return unknown.Count > 0
      ? Fail(new DomainError("UnknownParameter",
          $"Unknown parameter(s): {string.Join(", ", unknown)}. " +
          $"This tool takes no parameters besides {ToolTimeout.ParameterName}."))
      : Result.Success(true);
  }

  private static Result<bool> Fail(DomainError err) => Result.Failure<bool>(err);

  private static ToolResult Err(DomainError error) => new($"Error [{error.Code}]: {error.Message}", true);
}
