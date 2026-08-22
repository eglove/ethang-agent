using System.Text.Json;
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>The memory capability surface: recall searches persisted transcripts across
///     sessions, sessions lists what conversations exist. Read-only this phase — search
///     and listing live behind the query seams; this provider owns strict argument
///     parsing and the verbatim output contracts only.</summary>
public sealed class MemoryCapabilityProvider(IMemoryRecallQuery recallQuery, IMemorySessionsQuery sessionsQuery)
    : ICapabilityProvider
{
    public const string ProviderId = "memory";

    private const int MaxSnippetLength = 120;

    private readonly IMemoryRecallQuery _recallQuery =
        recallQuery ?? throw new ArgumentNullException(nameof(recallQuery));
    private readonly IMemorySessionsQuery _sessionsQuery =
        sessionsQuery ?? throw new ArgumentNullException(nameof(sessionsQuery));

    public string Id => ProviderId;

    public IReadOnlyList<ActionDescriptor> Actions { get; } =
    [
        new("recall", "Search persisted conversation transcripts across sessions.",
            """
            All arguments optional except none; empty query browses. Hits render annotation-style, one per line: [mem] session=<id> seq=<n> role=<r> <snippet≤120 chars>, followed by a --- memory: <total> hits, page <p>/<pages> --- footer. Regex failures render their typed error line.
            Output contract:
            [mem] session=<id> seq=<n> role=<r> <snippet≤120 chars>
            --- memory: <total> hits, page <p>/<pages> ---
            Unknown arguments, wrong-typed arguments, and invalid scope/queryMode/branches/role/page/pageSize values return typed 'Error [Code]: message' lines naming the valid spellings.
            """,
            [
                new ActionParameter("query", "String", "Optional. Literal search text (whitespace-split tokens ANDed) or regex source when queryMode='regex'; omit or leave empty to browse newest-first."),
                new ActionParameter("queryMode", "String", "Optional. 'literal' (default) or 'regex'. Literal input is never compiled as regex."),
                new ActionParameter("scope", "String", "Optional. 'global' (default) or 'session:<agentId>'."),
                new ActionParameter("branches", "String", "Optional. 'active' (default) keeps sessions whose lineage reaches a root; 'all' spans every persisted session."),
                new ActionParameter("role", "String", "Optional. Filter hits to 'user', 'assistant', or 'tool'."),
                new ActionParameter("page", "Number", "Optional. 1-based page number (default 1)."),
                new ActionParameter("pageSize", "Number", "Optional. Hits per page, 1..200 (default 25)."),
            ]),
        new("sessions", "List persisted conversation sessions.",
            """
            One line per session, newest first: session=<id> label=<label> depth=<d> entries=<n> status=<s> tier=hot. tier is always hot — every persisted session is fully indexed.
            Output contract:
            session=<id> label=<label> depth=<d> entries=<n> status=<s> tier=hot
            Unknown arguments, wrong-typed arguments, and invalid scope/branches/limit values return typed 'Error [Code]: message' lines naming the valid spellings.
            """,
            [
                new ActionParameter("scope", "String", "Optional. 'global' (default) or 'session:<agentId>'."),
                new ActionParameter("branches", "String", "Optional. 'active' (default) or 'all'."),
                new ActionParameter("limit", "Number", "Optional. Maximum sessions listed, 1..500 (default 50)."),
            ]),
    ];

    public async Task<CapabilityInvocationResult> InvokeAsync(
        string actionName, string jsonArguments, CancellationToken ct = default)
    {
        return actionName switch
        {
            "recall" => await Recall(jsonArguments, ct),
            "sessions" => await Sessions(jsonArguments, ct),
            _ => CapabilityInvocationResult.Fail($"Error [UnknownAction]: Unknown action: {actionName}."),
        };
    }

    private async Task<CapabilityInvocationResult> Recall(string json, CancellationToken ct)
    {
        var root = ParseObjectRoot(json);
        if (!root.IsSuccess)
            return Fail(root.Error!);

        var allowed = new HashSet<string>(StringComparer.Ordinal)
            { "query", "queryMode", "scope", "branches", "role", "page", "pageSize" };
        if (UnknownArgument(root.Value!, allowed) is { } unknown)
            return Fail(new Error("InvalidArgument", $"unknown argument '{unknown}'."));

        var query = OptionalString(root.Value!, "query");
        if (!query.IsSuccess) return Fail(query.Error!);
        var queryMode = OptionalString(root.Value!, "queryMode");
        if (!queryMode.IsSuccess) return Fail(queryMode.Error!);
        var scope = OptionalString(root.Value!, "scope");
        if (!scope.IsSuccess) return Fail(scope.Error!);
        var branches = OptionalString(root.Value!, "branches");
        if (!branches.IsSuccess) return Fail(branches.Error!);
        var role = OptionalString(root.Value!, "role");
        if (!role.IsSuccess) return Fail(role.Error!);
        var page = OptionalNumber(root.Value!, "page", fallback: 1);
        if (!page.IsSuccess) return Fail(page.Error!);
        var pageSize = OptionalNumber(root.Value!, "pageSize", fallback: 25);
        if (!pageSize.IsSuccess) return Fail(pageSize.Error!);

        var recalled = await _recallQuery.Execute(
            query.Value, queryMode.Value ?? "literal", scope.Value ?? "global",
            branches.Value ?? "active", role.Value, page.Value, pageSize.Value, ct);
        return recalled.IsSuccess
            ? CapabilityInvocationResult.Ok(RenderPage(recalled.Value!))
            : Fail(recalled.Error!);
    }

    private async Task<CapabilityInvocationResult> Sessions(string json, CancellationToken ct)
    {
        var root = ParseObjectRoot(json);
        if (!root.IsSuccess)
            return Fail(root.Error!);

        var allowed = new HashSet<string>(StringComparer.Ordinal) { "scope", "branches", "limit" };
        if (UnknownArgument(root.Value!, allowed) is { } unknown)
            return Fail(new Error("InvalidArgument", $"unknown argument '{unknown}'."));

        var scope = OptionalString(root.Value!, "scope");
        if (!scope.IsSuccess) return Fail(scope.Error!);
        var branches = OptionalString(root.Value!, "branches");
        if (!branches.IsSuccess) return Fail(branches.Error!);
        var limit = OptionalNumber(root.Value!, "limit", fallback: 50);
        if (!limit.IsSuccess) return Fail(limit.Error!);

        var listed = await _sessionsQuery.Execute(
            scope.Value ?? "global", branches.Value ?? "active", limit.Value, ct);
        return listed.IsSuccess
            ? CapabilityInvocationResult.Ok(RenderSummaries(listed.Value!))
            : Fail(listed.Error!);
    }

    /// <summary>Renders the recall output contract: one annotation line per hit, then the
    ///     paging footer. A zero-hit page renders the footer alone.</summary>
    private static string RenderPage(RecallPage page)
    {
        List<string> lines = [.. page.Hits.Select(hit =>
            $"[mem] session={hit.Session} seq={hit.Seq} role={hit.Role} {Snippet(hit.Content)}")];
        lines.Add($"--- memory: {page.TotalMatched} hits, page {page.Page}/{page.Pages} ---");
        return string.Join("\n", lines);
    }

    /// <summary>Newlines collapse to single spaces so one hit stays exactly one output line;
    ///     longer content truncates at 120 characters rather than wrapping.</summary>
    private static string Snippet(string content)
    {
        var collapsed = content.Replace('\r', ' ').Replace('\n', ' ');
        return collapsed.Length <= MaxSnippetLength ? collapsed : collapsed[..MaxSnippetLength];
    }

    private static string RenderSummaries(IReadOnlyList<SessionSummary> summaries)
        => string.Join("\n", summaries.Select(summary =>
            $"session={summary.Id} label={summary.Label} depth={summary.Depth} " +
            $"entries={summary.EntryCount} status={summary.Status} tier={summary.Tier}"));

    private static CapabilityInvocationResult Fail(Error error)
        => CapabilityInvocationResult.Fail($"Error [{error.Code}]: {error.Message}");

    /// <summary>Arguments cross into the queries strictly: a JSON object root, known keys
    ///     only, exact JSON types. Nothing external becomes load-bearing here — parse
    ///     shapes are typed errors, never coercion.</summary>
    private static Result<JsonElement> ParseObjectRoot(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return Result<JsonElement>.Failure(new Error("InvalidActionInput",
                "arguments must be a valid JSON object."));
        }

        using (doc)
        {
            return doc.RootElement.ValueKind is JsonValueKind.Object
                ? Result<JsonElement>.Success(doc.RootElement.Clone())
                : Result<JsonElement>.Failure(new Error("InvalidArgument",
                    "arguments must be a JSON object."));
        }
    }

    private static string? UnknownArgument(JsonElement args, IReadOnlySet<string> allowed)
    {
        // JsonProperty is a struct: FirstOrDefault on an exhausted sequence yields
        // default(JsonProperty), and reading .Name off it throws — so enumerate manually.
        foreach (var property in args.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                return property.Name;
        }

        return null;
    }

    private static Result<string?> OptionalString(JsonElement args, string key)
    {
        if (!args.TryGetProperty(key, out var element))
            return Result<string?>.Success(null);

        return element.ValueKind is JsonValueKind.String
            ? Result<string?>.Success(element.GetString())
            : Result<string?>.Failure(new Error("InvalidArgument",
                $"argument '{key}' must be a string."));
    }

    private static Result<int> OptionalNumber(JsonElement args, string key, int fallback)
    {
        if (!args.TryGetProperty(key, out var element))
            return Result<int>.Success(fallback);

        return element.ValueKind is JsonValueKind.Number && element.TryGetInt32(out var value)
            ? Result<int>.Success(value)
            : Result<int>.Failure(new Error("InvalidArgument",
                $"argument '{key}' must be a number."));
    }
}
