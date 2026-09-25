using eThangAgent.SkillDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Searches the skills.sh directory (plan #29 task 9). Read-only.
/// Output contract (verbatim, pinned by tests): header, up to fifteen
/// numbered entries, a visible truncation marker, a no-results line.</summary>
public sealed class SkillSearchTool(SkillRegistryService registry) : ITool
{
  private const int MaxEntries = 15;

  private readonly SkillRegistryService _registry = registry ?? throw new ArgumentNullException(nameof(registry));

  public ToolDefinition Definition { get; } = new(
      "skill_search",
      "Search the skills.sh directory for community skills. timeoutSeconds and query are mandatory. " +
      "Output: a header '[skill search: <query>]', then up to 15 lines 'N. <name> — <installs> installs — <address>' " +
      "(the address is owner/repo or owner/repo/skill and is used directly as skill_registry Install's address), " +
      "then '+N more (call skill_search again to refine)' when truncated; '[skill search: <query>] no results' when empty. " +
      "Errors begin with `Error [Code]:`.",
      [
        new ToolParameter("query", ToolParameterType.Text, "Search terms (1-200 characters)."),
      ],
      ["timeoutSeconds", "query"]);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return budget.IsSuccess
        ? ToolExecution.RunAsync(input.Name, budget.Value.Timeout, SearchCoreAsync(input), ct)
        : Task.FromResult(Fail(budget.Error));
  }

  private Func<CancellationToken, Task<ToolResult>> SearchCoreAsync(RawToolInput input) => async token =>
  {
    Result<string> query = SkillRegistryInputs.ParseQuery(System.Text.Json.JsonDocument.Parse(input.JsonArguments).RootElement);
    if (!query.IsSuccess)
    {
      return Fail(query.Error);
    }

    Result<IReadOnlyList<SkillsShEntry>> r = await _registry.SearchAsync(query.Value, token).ConfigureAwait(false);
    return r.IsSuccess ? Ok(Render(query.Value, r.Value)) : Fail(r.Error);
  };

  internal static string Render(string query, IReadOnlyList<SkillsShEntry> entries)
  {
    if (entries.Count == 0)
    {
      return $"[skill search: {query}] no results";
    }

    List<string> lines = [$"[skill search: {query}]"];
    int n = 0;
    foreach (SkillsShEntry entry in entries.Take(MaxEntries))
    {
      n++;
      lines.Add($"{n}. {entry.Name} — {entry.Installs} installs — {entry.Address}");
    }

    if (entries.Count > MaxEntries)
    {
      lines.Add($"+{entries.Count - MaxEntries} more (call skill_search again to refine)");
    }

    return string.Join("\n", lines);
  }

  private static ToolResult Ok(string content) => new(content, IsError: false);

  private static ToolResult Fail(DomainError error) => new($"Error [{error.Code}]: {error.Message}", IsError: true);
}