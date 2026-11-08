using System.Text.Json;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.ToolDomain;

public sealed class SkillListTool(ISkillCatalog catalog) : ITool
{
  // Keep in sync: format parity with SkillsListingPromptProvider truncation
  // (SkillListingBudget.DescriptionLimit in eThangAgent.Composition) - Tool.Domain
  // cannot reference Composition, so the constant is duplicated deliberately.
  // The listing's truncation marker sends the model HERE for the full catalog, so this
  // surface must not cut what the listing was told to stop cutting: a cap that fires on
  // an ordinary description makes the pointer a dead end. The real governor is the
  // listing's MaxChars budget, not this constant.
  private const int DescriptionLimit = 200;

  private readonly ISkillCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

  public ToolDefinition Definition { get; } = new(
      "skill_list",
      "List available methodology skills — built-ins shipped with the app and skills " +
      "loaded from configured skill directories — merged and sorted by name. Takes no " +
      "parameters besides the mandatory timeoutSeconds budget; other arguments are " +
      "rejected. Output is one header line `[skills: N available]`, then one line per " +
      "skill: `<name> <builtin|file> v<version>[ [manual]]  <description>` — the name " +
      "padded to 20 characters, the source label `builtin` for built-ins and `file` for " +
      "file skills, the literal ` [manual]` marker between the version and the " +
      "description separator only for manual-invocation-only skills, and the description " +
      "truncated to 200 characters with an appended … when longer. After the skill rows " +
      "the catalog's diagnostics render: each collision line verbatim with its " +
      "`[collision] ` prefix, every other diagnostic as `[warning] <text>`. A source " +
      "failure — `[warning] built-in skills unavailable: <reason>` — keeps its position " +
      "after the diagnostics: an unreadable source's skills are omitted while the " +
      "listing itself still succeeds. Errors begin with `Error [Code]:`.",
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

    Result<IReadOnlyList<SkillDefinition>> listed = await _catalog.ListAsync(ct).ConfigureAwait(false);
    if (listed.IsSuccess)
    {
      skills.AddRange(listed.Value);
    }
    else
    {
      warnings.Add($"[warning] built-in skills unavailable: {listed.Error.Message}");
    }

    List<string> diagnostics = await GetDiagnosticsAsync(ct).ConfigureAwait(false);

    List<string> lines =
    [
      $"[skills: {skills.Count} available]",
      .. skills
          .OrderBy(s => s.Name, StringComparer.Ordinal)
          .Select(FormatRow),
      .. diagnostics,
      .. warnings,
    ];

    return new ToolResult(string.Join("\n", lines), false);
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
    // Unnamed enum values cannot occur.
    _ => "file",
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
