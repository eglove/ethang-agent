using eThangAgent.AgentDomain;
using eThangAgent.MemoryDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Memory;

/// <summary>Read side of the memory CQRS split: resolves scope and branch lineage against
///     the agent store, builds the session corpora, plans the query once, searches, and
///     projects one paged result. Wire input is validated strictly here — unknown values are
///     typed errors naming valid spellings, never silent fallbacks.</summary>
public sealed class RecallQueryHandler
{
    private readonly IAgentStore _store;

    public RecallQueryHandler(IAgentStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <param name="query">Null or whitespace browses newest-first; literal input is tokenized,
    ///     never compiled as regex.</param>
    /// <param name="queryMode">Exactly "literal" or "regex".</param>
    /// <param name="scope">Null/"global" or "session:&lt;agentId&gt;" (exact 'D' guid format).</param>
    /// <param name="branches">Exactly "active" or "all".</param>
    /// <param name="role">Null, or one of user/assistant/tool in any casing.</param>
    public async Task<Result<RecallPage>> Execute(
        string? query, string queryMode, string? scope, string branches, string? role,
        int page, int pageSize)
    {
        if (page < 1)
            return InvalidArgument("page must be at least 1.");
        if (pageSize < 1 || pageSize > 200)
            return InvalidArgument("pageSize must be between 1 and 200.");

        var parsedScope = SessionScope.Parse(scope);
        if (!parsedScope.IsSuccess)
            return Result<RecallPage>.Failure(parsedScope.Error!);

        if (!string.Equals(queryMode, "literal", StringComparison.Ordinal) &&
            !string.Equals(queryMode, "regex", StringComparison.Ordinal))
            return InvalidArgument("queryMode must be 'literal' or 'regex'.");

        if (role is not null &&
            !string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
            return InvalidArgument("role must be 'user', 'assistant', or 'tool'.");

        var branchMode = branches switch
        {
            "active" => BranchMode.ActivePath,
            "all" => BranchMode.AllBranches,
            _ => (BranchMode?)null,
        };
        if (branchMode is not { } mode)
            return InvalidArgument("branches must be 'active' or 'all'.");

        var corpora = await BuildCorporaAsync(parsedScope.Value!);
        if (!corpora.IsSuccess)
            return Result<RecallPage>.Failure(corpora.Error!);

        var plan = MemoryQueryPlan.Plan(query, queryMode);
        var outcome = new SearchService().Search(
            corpora.Value!, plan, parsedScope.Value!, mode, role, page, pageSize);

        return outcome switch
        {
            SearchOutcome.Ok ok => Result<RecallPage>.Success(Project(ok.Result)),
            SearchOutcome.Fail fail => Result<RecallPage>.Failure(FromRenderedLine(fail.Error)),
            _ => throw new NotSupportedException($"Unhandled search outcome {outcome.GetType().Name}."),
        };
    }

    private static RecallPage Project(SearchResult result)
    {
        var hits = result.Hits
            .Select(hit =>
            {
                var entry = hit.Entry;
                return new RecallPage.Hit(
                    entry.Session, entry.Seq, entry.Role, entry.Content, entry.Timestamp);
            })
            .ToList();
        return new RecallPage(hits, result.TotalMatched, result.Page, result.Pages);
    }

    private static Result<RecallPage> InvalidArgument(string message)
        => Result<RecallPage>.Failure(new Error("InvalidArgument", message));

    private async Task<Result<List<SessionCorpus>>> BuildCorporaAsync(SessionScope scope)
    {
        var records = scope switch
        {
            SessionScope.Global => await _store.ListAllAsync(),
            SessionScope.Session session => (await _store.GetAsync(session.Id))
                .Map(single => (IReadOnlyList<AgentRecord>)[single]),
            _ => throw new NotSupportedException($"Unhandled scope type {scope.GetType().Name}."),
        };
        return await FromRecordsAsync(records);
    }

    /// <summary>Turns store rows into corpora; every store failure surfaces untouched.</summary>
    private async Task<Result<List<SessionCorpus>>> FromRecordsAsync(
        Result<IReadOnlyList<AgentRecord>> records)
    {
        if (!records.IsSuccess)
            return Result<List<SessionCorpus>>.Failure(records.Error!);

        List<SessionCorpus> corpora = [];
        foreach (var record in records.Value!)
        {
            var transcript = await _store.GetTranscriptAsync(record.Id);
            if (!transcript.IsSuccess)
                return Result<List<SessionCorpus>>.Failure(transcript.Error!);

            corpora.Add(new SessionCorpus(
                record.Id,
                record.ParentId,
                record.Depth,
                [.. transcript.Value!.Select((message, index) => new MemoryEntry(
                    record.Id, index, message.Role.ToString(), message.Content, message.Timestamp))]));
        }

        return Result<List<SessionCorpus>>.Success(corpora);
    }

    /// <summary>Unwraps <see cref="SearchOutcome.Fail"/>: it carries the already-rendered typed
    ///     error line ("Error [code]: message"), while this handler's failures are structured
    ///     <see cref="Error"/> pairs rendered to the same shape at the capability edge — so the
    ///     line is split back into its pair, preserving it verbatim through one more hop.</summary>
    private static Error FromRenderedLine(string renderedLine)
    {
        const string head = "Error [";
        if (!renderedLine.StartsWith(head, StringComparison.Ordinal))
            throw new FormatException(
                $"Search failure line does not start with '{head}': {renderedLine}");

        var codeEnd = renderedLine.IndexOf(']', head.Length);
        if (codeEnd < 0)
            throw new FormatException(
                $"Search failure line has no closing bracket: {renderedLine}");

        var code = renderedLine[head.Length..codeEnd];
        var remainder = renderedLine[(codeEnd + 1)..];
        var message = remainder.StartsWith(": ", StringComparison.Ordinal)
            ? remainder[2..]
            : remainder;
        return new Error(code, message);
    }
}
