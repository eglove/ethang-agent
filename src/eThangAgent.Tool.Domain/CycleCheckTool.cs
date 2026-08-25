using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
            new ToolParameter("edges", ToolParameterType.StringArray,
                "Dependency edges: objects with 'from', 'to' (unit-name strings) and 'deferred' (bool). At least one required."),
            new ToolParameter("entry", ToolParameterType.StringArray,
                "Entry points that start resolution; omit to check the whole graph."),
            new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.Integer, ToolTimeout.ParameterDescription, Minimum: 1),
        ],
        ["edges", "timeoutSeconds"]);

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
        var parsed = ParseArguments(input.JsonArguments);
        if (!parsed.IsSuccess)
            return Task.FromResult(Err(parsed.Error!));

        var (_, entries, edges) = parsed.Value!;
        return ToolExecution.RunAsync(input.Name, ToolTimeout.Parse(parsed.Value!.Json).Value, _ =>
            Task.FromResult(Analyze(entries, edges)), ct);
    }

    private static ToolResult Analyze(IReadOnlyList<string> entries, IReadOnlyList<DependencyEdge> edges)
    {
        var report = CycleDetector.Detect(entries, edges);
        if (!report.IsSuccess)
            return Err(report.Error!);
        var r = report.Value!;

        var nodeCount = edges.SelectMany(e => new[] { e.From, e.To }).Distinct().Count();
        var lines = new List<string>
        {
            $"[cycle-check: {nodeCount} units, {edges.Count} edges, {entries.Count} entry point{(entries.Count == 1 ? "" : "s")}]",
        };
        foreach (var c in r.ReachableCycles)
        {
            var chain = string.Join(" -> ", c.Members.Concat(new[] { c.Members[0] }));
            lines.Add(c.Verdict == CycleVerdict.DeadlockRisk
                ? $"[cycle] {chain} — contains all-eager cycle: deadlock-risk"
                : $"[cycle] {chain} — all edges deferred: latent (safe until a deferral is removed)");
        }
        if (r.ReachableCycles.Count == 0)
            lines.Add("[ok] no dependency cycles");
        if (r.UnreachableCycles > 0)
            lines.Add($"[unreachable cycles: {r.UnreachableCycles}]");

        return new ToolResult(string.Join("\n", lines), false);
    }

    private static Result<ParsedArgs> ParseArguments(string jsonArguments)
    {
        var baseParse = ToolArguments.ParseObject(jsonArguments);
        if (!baseParse.IsSuccess)
            return Result<ParsedArgs>.Failure(baseParse.Error!);
        var json = baseParse.Value;

        if (!json.TryGetProperty("edges", out var rawEdges) || rawEdges.ValueKind != JsonValueKind.Array)
            return Fail("MissingEdges", "'edges' must be an array of {from, to, deferred} objects.");
        var edges = new List<DependencyEdge>();
        foreach (var e in rawEdges.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object ||
                !e.TryGetProperty("from", out var f) || f.ValueKind != JsonValueKind.String ||
                !e.TryGetProperty("to", out var t) || t.ValueKind != JsonValueKind.String ||
                !e.TryGetProperty("deferred", out var d) || d.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return Fail("MalformedEdge", "Every edge must be an object with string 'from', string 'to', boolean 'deferred'.");
            if (f.GetString()!.Length == 0 || t.GetString()!.Length == 0)
                return Fail("EmptyUnitName", "Unit names must be non-empty strings.");
            edges.Add(new DependencyEdge(f.GetString()!, t.GetString()!, d.GetBoolean()));
        }
        if (edges.Count == 0)
            return Fail("MissingEdges", "'edges' must contain at least one edge.");

        var entries = new List<string>();
        if (json.TryGetProperty("entry", out var rawEntry))
        {
            if (rawEntry.ValueKind != JsonValueKind.Array ||
                rawEntry.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
                return Fail("InvalidEntry", "'entry' must be an array of unit-name strings.");
            entries.AddRange(rawEntry.EnumerateArray().Select(x => x.GetString()!));
            if (entries.Any(n => n.Length == 0))
                return Fail("EmptyUnitName", "Entry-point names must be non-empty strings.");
        }

        var budget = ToolTimeout.Parse(json);
        if (!budget.IsSuccess)
            return Result<ParsedArgs>.Failure(budget.Error!);

        return Result<ParsedArgs>.Success(new ParsedArgs(json, entries, edges));
    }

    private sealed record ParsedArgs(JsonElement Json, IReadOnlyList<string> Entries, IReadOnlyList<DependencyEdge> Edges);

    private static Result<ParsedArgs> Fail(string code, string message) =>
        Result<ParsedArgs>.Failure(new Error(code, message));

    private static ToolResult Err(Error error) => new($"Error [{error.Code}]: {error.Message}", true);
}
