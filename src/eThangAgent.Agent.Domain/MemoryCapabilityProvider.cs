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
  private const string Scope = "scope";
  private const string Branches = "branches";
  private const string InvalidArgument = "InvalidArgument";

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
                new ActionParameter("query", ActionParameterTypes.StringType, "Optional. Literal search text (whitespace-split tokens ANDed) or regex source when queryMode='regex'; omit or leave empty to browse newest-first."),
                new ActionParameter("queryMode", ActionParameterTypes.StringType, "Optional. 'literal' (default) or 'regex'. Literal input is never compiled as regex."),
                new ActionParameter(Scope, ActionParameterTypes.StringType, "Optional. 'global' (default) or 'session:<agentId>'."),
                new ActionParameter(Branches, ActionParameterTypes.StringType, "Optional. 'active' (default) keeps sessions whose lineage reaches a root; 'all' spans every persisted session."),
                new ActionParameter("role", ActionParameterTypes.StringType, "Optional. Filter hits to 'user', 'assistant', or 'tool'."),
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
                new ActionParameter(Scope, ActionParameterTypes.StringType, "Optional. 'global' (default) or 'session:<agentId>'."),
                new ActionParameter(Branches, ActionParameterTypes.StringType, "Optional. 'active' (default) or 'all'."),
                new ActionParameter("limit", "Number", "Optional. Maximum sessions listed, 1..500 (default 50)."),
            ]),
    ];

  public async Task<CapabilityInvocationResult> InvokeAsync(
      string actionName, string jsonArguments, CancellationToken ct = default)
  {
    return actionName switch
    {
      "recall" => await Recall(jsonArguments, ct).ConfigureAwait(false),
      "sessions" => await Sessions(jsonArguments, ct).ConfigureAwait(false),
      _ => CapabilityInvocationResult.Fail($"Error [UnknownAction]: Unknown action: {actionName}."),
    };
  }

  private async Task<CapabilityInvocationResult> Recall(string json, CancellationToken ct)
  {
    Result<JsonElement> root = ParseObjectRoot(json);
    if (!root.IsSuccess)
    {
      return Fail(root.Error);
    }

    HashSet<string> allowed = new(StringComparer.Ordinal)
            { "query", "queryMode", Scope, Branches, "role", "page", "pageSize" };
    if (UnknownArgument(root.Value, allowed) is { } unknown)
    {
      return Fail(new DomainError(InvalidArgument, $"unknown argument '{unknown}'."));
    }

    // OptionalString/OptionalNumber already produce InvalidArgument errors describing
    // the key: the sequential validations collapse to one args-or-error helper.
    (RecallArgs args, DomainError? argsError) = ReadRecallArgs(root.Value);
    if (argsError is { } invalid)
    {
      return Fail(invalid);
    }
    Result<RecallPage> recalled = await _recallQuery.Execute(
        new RecallRequest(args.Query, args.QueryMode ?? "literal", args.ScopeText ?? "global",
            args.BranchesText ?? "active", args.Role, args.Page, args.PageSize),
        ct).ConfigureAwait(false);
    return recalled.IsSuccess
        ? CapabilityInvocationResult.Ok(RenderPage(recalled.Value))
        : Fail(recalled.Error);
  }

  private sealed record RecallArgs(
      string? Query, string? QueryMode, string? ScopeText, string? BranchesText, string? Role, int Page, int PageSize);

  /// <summary>Reads the recall arguments' seven optional values in the original
  ///     validation order: the args on success, the FIRST failure's error otherwise.</summary>
  private static (RecallArgs Args, DomainError? Error) ReadRecallArgs(JsonElement root)
  {
    Result<string?> query = OptionalString(root, "query");
    Result<string?> queryMode = OptionalString(root, "queryMode");
    Result<string?> scope = OptionalString(root, Scope);
    Result<string?> branches = OptionalString(root, Branches);
    Result<string?> role = OptionalString(root, "role");
    Result<int> page = OptionalNumber(root, "page", fallback: 1);
    Result<int> pageSize = OptionalNumber(root, "pageSize", fallback: 25);

    // First failure wins, in the original order (each Result carries ITS error; the
    // per-result Error property is the failure's DomainError, or null).
    DomainError?[] errors = [query.Error, queryMode.Error, scope.Error, branches.Error, role.Error, page.Error, pageSize.Error];
    DomainError? failure = errors.FirstOrDefault(e => e is not null);
    if (failure is not null)
    {
      return (new RecallArgs(null, null, null, null, null, 1, 25), failure);
    }

    return (new RecallArgs(query.Value, queryMode.Value, scope.Value, branches.Value,
        role.Value, page.Value, pageSize.Value), null);
  }

  private async Task<CapabilityInvocationResult> Sessions(string json, CancellationToken ct)
  {
    Result<JsonElement> root = ParseObjectRoot(json);
    if (!root.IsSuccess)
    {
      return Fail(root.Error);
    }

    HashSet<string> allowed = new(StringComparer.Ordinal) { Scope, Branches, "limit" };
    if (UnknownArgument(root.Value, allowed) is { } unknown)
    {
      return Fail(new DomainError(InvalidArgument, $"unknown argument '{unknown}'."));
    }

    Result<string?> scope = OptionalString(root.Value, Scope);
    if (!scope.IsSuccess)
    {
      return Fail(scope.Error);
    }

    Result<string?> branches = OptionalString(root.Value, Branches);
    if (!branches.IsSuccess)
    {
      return Fail(branches.Error);
    }

    Result<int> limit = OptionalNumber(root.Value, "limit", fallback: 50);
    if (!limit.IsSuccess)
    {
      return Fail(limit.Error);
    }

    Result<IReadOnlyList<SessionSummary>> listed = await _sessionsQuery.Execute(
        scope.ValueOrNull ?? "global", branches.ValueOrNull ?? "active", limit.Value, ct).ConfigureAwait(false);
    return listed.IsSuccess
        ? CapabilityInvocationResult.Ok(RenderSummaries(listed.Value))
        : Fail(listed.Error);
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
    string collapsed = content.Replace('\r', ' ').Replace('\n', ' ');
    return collapsed.Length <= MaxSnippetLength ? collapsed : collapsed[..MaxSnippetLength];
  }

  private static string RenderSummaries(IReadOnlyList<SessionSummary> summaries)
      => string.Join("\n", summaries.Select(summary =>
          $"session={summary.Id} label={summary.Label} depth={summary.Depth} " +
          $"entries={summary.EntryCount} status={summary.Status} tier={summary.Tier}"));

  private static CapabilityInvocationResult Fail(DomainError error)
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
      return Result.Failure<JsonElement>(new DomainError("InvalidActionInput",
          "arguments must be a valid JSON object."));
    }

    using (doc)
    {
      return doc.RootElement.ValueKind is JsonValueKind.Object
          ? Result.Success(doc.RootElement.Clone())
          : Result.Failure<JsonElement>(new DomainError(InvalidArgument,
              "arguments must be a JSON object."));
    }
  }

  private static string? UnknownArgument(JsonElement args, HashSet<string> allowed)
  {
    // Project to .Name (a reference type) before FirstOrDefault: JsonProperty itself is
    // a struct, and reading .Name off default(JsonProperty) on an exhausted sequence throws.
    return args.EnumerateObject()
        .Select(p => p.Name)
        .FirstOrDefault(name => !allowed.Contains(name));
  }

  private static Result<string?> OptionalString(JsonElement args, string key)
  {
    if (!args.TryGetProperty(key, out JsonElement element))
    {
      return Result.Success<string?>(null);
    }

    bool isString = element.ValueKind is JsonValueKind.String;
    return isString
        ? Result.Success(element.GetString())
        : Result.Failure<string?>(new DomainError(InvalidArgument,
            $"argument '{key}' must be a string."));
  }

  private static Result<int> OptionalNumber(JsonElement args, string key, int fallback)
  {
    if (!args.TryGetProperty(key, out JsonElement element))
    {
      return Result.Success(fallback);
    }

    if (element.ValueKind is not JsonValueKind.Number)
    {
      return Result.Failure<int>(new DomainError(InvalidArgument,
          $"argument '{key}' must be a number."));
    }

    int? parsed = element.TryGetInt32(out int value) ? value : null;
    return parsed is not null
        ? Result.Success(parsed.Value)
        : Result.Failure<int>(new DomainError(InvalidArgument,
            $"argument '{key}' must be a number."));
  }
}
