using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Detects dependency cycles in a caller-supplied construction graph and
///     classifies each as deadlock-risk or latent. Language-agnostic: the caller (the
///     model, while reading a repo) resolves wiring into an edge list — every ecosystem's
///     DI/factory/import idiom reduces to "who can construct whom" plus "immediately or
///     on demand". Recipe: harvest candidate edges with universal structural patterns
///     (construction inside a registered factory/lazy/provider/lookup = deferred edge;
///     direct top-level construction = eager), resolve names to stable unit names, pass
///     them here. The tool owns only the graph math.</summary>
public sealed class CycleCheckTool : ITool
{
  public ToolDefinition Definition { get; } = new(
      "cycle_check",
      "Detect dependency cycles in a construction graph and classify deadlock risk. " +
      "Language-agnostic: you supply an edge list of named units; each edge says whether " +
      "the target is constructed eagerly (immediately when the source constructs) or " +
      "deferred (factory/lazy/provider/lookup-by-name — constructed later on demand). " +
      "Recipe: while reading a repo, harvest edges with these universal shapes — " +
      "(1) a constructor/factory call inside a registered factory callback, lazy wrapper, " +
      "provider, getter, or string-based lookup is a DEFERRED edge; (2) direct construction " +
      "at registration/module top level is an EAGER edge; (3) resolve every reference to a " +
      "stable unit name so the same thing is always the same name. Then submit the list here.\n\n" +
      "Arguments: 'edges': array of objects with keys 'from' (string), 'to' (string), and " +
      "'deferred' (boolean); optional 'entry': array of unit names whose resolution starts " +
      "the graph (defaults to every node, i.e. check everything); mandatory 'timeoutSeconds'.\n\n" +
      "Output format (verbatim): first line `[cycle-check: N units, M edges, K entry point(s)]`; " +
      "then per detected cycle one line `[cycle] A -> B -> A — contains all-eager cycle: deadlock-risk` " +
      "or `[cycle] A -> B -> A — all edges deferred: latent (safe until a deferral is removed)`; " +
      "cycles not reachable from any entry point are counted in a trailing " +
      "`[unreachable cycles: N]` line and omitted individually; a clean graph reports " +
      "`[ok] no dependency cycles`. Errors begin with `Error [Code]:`.",
      [
          new ToolParameter("edges", ToolParameterType.TextArray,
                "Dependency edges: objects with 'from', 'to' (unit-name strings) and 'deferred' (bool). At least one required."),
            new ToolParameter("entry", ToolParameterType.TextArray,
                "Entry points that start resolution; omit to check the whole graph."),
            new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
      ],
      ["edges", "timeoutSeconds"]);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<ParsedArgs> parsed = ParseArguments(input.JsonArguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(Err(parsed.Error));
    }

    (JsonElement _, IReadOnlyList<string>? entries, IReadOnlyList<DependencyEdge>? edges) = parsed.Value;
    return ToolExecution.RunAsync(input.Name, ToolTimeout.Parse(parsed.Value.Json).Value, _ =>
        Task.FromResult(Analyze(entries, edges)), ct);
  }

  private static ToolResult Analyze(IReadOnlyList<string> entries, IReadOnlyList<DependencyEdge> edges)
  {
    Result<CycleReport> report = CycleDetector.Detect(entries, edges);
    if (!report.IsSuccess)
    {
      return Err(report.Error);
    }

    CycleReport r = report.Value;

    int nodeCount = edges.SelectMany(e => new[] { e.From, e.To }).Distinct().Count();
    List<string> lines =
    [
            $"[cycle-check: {nodeCount} units, {edges.Count} edges, {entries.Count} entry point{(entries.Count == 1 ? "" : "s")}]",
        ];
    foreach (DetectedCycle c in r.ReachableCycles)
    {
      string chain = string.Join(" -> ", c.Members.Concat([c.Members[0]]));
      lines.Add(c.Verdict == CycleVerdict.DeadlockRisk
          ? $"[cycle] {chain} — contains all-eager cycle: deadlock-risk"
          : $"[cycle] {chain} — all edges deferred: latent (safe until a deferral is removed)");
    }
    if (r.ReachableCycles.Count == 0)
    {
      lines.Add("[ok] no dependency cycles");
    }

    if (r.UnreachableCycles > 0)
    {
      lines.Add($"[unreachable cycles: {r.UnreachableCycles}]");
    }

    return new ToolResult(string.Join("\n", lines), false);
  }

  private static Result<ParsedArgs> ParseArguments(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Result.Failure<ParsedArgs>(baseParse.Error);
    }

    JsonElement json = baseParse.Value;

    if (!json.TryGetProperty("edges", out JsonElement rawEdges) || rawEdges.ValueKind != JsonValueKind.Array)
    {
      return Fail("MissingEdges", "'edges' must be an array of {from, to, deferred} objects.");
    }

    Result<List<DependencyEdge>> edges = ParseEdges(rawEdges);
    if (!edges.IsSuccess)
    {
      return Result.Failure<ParsedArgs>(edges.Error);
    }

    if (edges.Value.Count == 0)
    {
      return Fail("MissingEdges", "'edges' must contain at least one edge.");
    }

    Result<List<string>> entries = ParseEntries(json);
    if (!entries.IsSuccess)
    {
      return Result.Failure<ParsedArgs>(entries.Error);
    }

    Result<TimeSpan> budget = ToolTimeout.Parse(json);
    Result<ParsedArgs> parsed = budget.IsSuccess
      ? Result.Success(new ParsedArgs(json, entries.Value, edges.Value))
      : Result.Failure<ParsedArgs>(budget.Error);
    return parsed;
  }

  private static Result<List<DependencyEdge>> ParseEdges(JsonElement rawEdges)
  {
    List<DependencyEdge> edges = [];
    foreach (JsonElement e in rawEdges.EnumerateArray())
    {
      DomainError? invalid = ParseEdge(e, edges);
      if (invalid is not null)
      {
        return Result.Failure<List<DependencyEdge>>(invalid);
      }
    }

    return Result.Success(edges);
  }

  /// <summary>One edge: object with string 'from', string 'to', boolean 'deferred',
  ///     both unit names non-empty.</summary>
  private static DomainError? ParseEdge(JsonElement e, List<DependencyEdge> edges)
  {
    if (e.ValueKind != JsonValueKind.Object ||
        !e.TryGetProperty("from", out JsonElement f) || f.ValueKind != JsonValueKind.String ||
        !e.TryGetProperty("to", out JsonElement t) || t.ValueKind != JsonValueKind.String ||
        !e.TryGetProperty("deferred", out JsonElement d) || d.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
    {
      return new DomainError("MalformedEdge", "Every edge must be an object with string 'from', string 'to', boolean 'deferred'.");
    }

    if (f.GetString()!.Length == 0 || t.GetString()!.Length == 0)
    {
      return new DomainError("EmptyUnitName", "Unit names must be non-empty strings.");
    }

    edges.Add(new DependencyEdge(f.GetString()!, t.GetString()!, d.GetBoolean()));
    return null;
  }

  private static Result<List<string>> ParseEntries(JsonElement json)
  {
    List<string> entries = [];
    if (!json.TryGetProperty("entry", out JsonElement rawEntry))
    {
      return Result.Success(entries);
    }

    if (rawEntry.ValueKind != JsonValueKind.Array ||
        rawEntry.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
    {
      return Result.Failure<List<string>>(new DomainError("InvalidEntry", "'entry' must be an array of unit-name strings."));
    }

    entries.AddRange(rawEntry.EnumerateArray().Select(x => x.GetString()!));
    if (entries.Any(n => n.Length == 0))
    {
      return Result.Failure<List<string>>(new DomainError("EmptyUnitName", "Entry-point names must be non-empty strings."));
    }

    Result<List<string>> result = Result.Success(entries);
    return result;
  }

  private sealed record ParsedArgs(JsonElement Json, IReadOnlyList<string> Entries, IReadOnlyList<DependencyEdge> Edges);

  private static Result<ParsedArgs> Fail(string code, string message) =>
      Result.Failure<ParsedArgs>(new DomainError(code, message));

  private static ToolResult Err(DomainError error) => new($"Error [{error.Code}]: {error.Message}", true);
}
