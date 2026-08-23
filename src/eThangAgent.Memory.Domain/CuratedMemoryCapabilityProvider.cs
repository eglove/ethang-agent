using System.Text.Json;
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.MemoryDomain;

/// <summary>
/// Model-facing capability surface over curated memories (provider id "memories"):
/// search / add / update / remove. Input parsing is strict — unknown parameters are
/// rejected and the enums are exact-lowercase wire forms; nothing is coerced except
/// the sanctioned benign-overshoot clamp on search limit (visible warning). Every
/// expected failure surfaces to the model as a typed "Error [Code]: ..." line so it
/// can self-correct. Provenance is ambient — the running session id, when one exists —
/// and is never accepted from the model. Successful adds tick the injected write
/// counter that drives turn-boundary nudges; nothing else does.
/// </summary>
public sealed class CuratedMemoryCapabilityProvider : ICapabilityProvider
{
    public const string ProviderId = "memories";

    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;
    private const int MaxTags = 12;
    private const int MaxHintChars = 200;
    private const int ContentPreviewChars = 120;
    private const int HintPreviewChars = 80;

    private readonly ICuratedMemoryStore _store;
    private readonly Func<string> _workspaceId;
    private readonly Func<string?> _provenance;
    private readonly Func<int> _bumpWrites;
    private readonly Func<DateTimeOffset> _clock;

    public CuratedMemoryCapabilityProvider(
        ICuratedMemoryStore store,
        Func<string> workspaceId,
        Func<string?> provenance,
        Func<int> bumpWrites,
        Func<DateTimeOffset> clock)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _workspaceId = workspaceId ?? throw new ArgumentNullException(nameof(workspaceId));
        _provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        _bumpWrites = bumpWrites ?? throw new ArgumentNullException(nameof(bumpWrites));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public string Id => ProviderId;

    public IReadOnlyList<ActionDescriptor> Actions { get; } =
    [
        new("search", "Search curated memories.",
            "Ranked hits: full-text match on query when given, otherwise newest-updated first. "
            + "All parameters are optional. Output format:\n"
            + "[memories] N hit(s)\n"
            + "[mem] id=<first8> v<n> cat=<category> scope=<scope> tags=t1,t2 :: <content <=120 chars>\n"
            + "     hint: <usage_hint <=80 chars>          (only when present)",
            [new ActionParameter("query", "String", "Optional full-text query."),
             new ActionParameter("category", "String", "Optional exact-lowercase filter: convention | preference | insight | failure | reference."),
             new ActionParameter("tags", "String[]", "Optional tag filters; rows must carry all of them."),
             new ActionParameter("scope", "String", "Optional exact filter: workspace | global."),
             new ActionParameter("limit", "Integer", "Optional. Default 20; minimum 1; values above 100 clamp to 100 with a visible [warning] line.")]),
        new("add", "Store a durable curated memory.",
            "Requires content (trimmed non-empty, at most 4000 chars), category (exact-lowercase), "
            + "and scope (workspace | global). Optional tags (at most 12, each matching "
            + "^[a-z0-9][a-z0-9-_]{0,31}$, deduplicated) and usage_hint (at most 200 chars). The "
            + "session of record is captured automatically; a 'session' parameter is rejected. Output: "
            + "'[memories] added <first8> v1 (cat=<c> scope=<s>)'.",
            [new ActionParameter("content", "String", "The memory itself; trimmed non-empty, max 4000 chars."),
             new ActionParameter("category", "String", "Exactly one of: convention | preference | insight | failure | reference."),
             new ActionParameter("tags", "String[]", "Optional; max 12 tags, lowercase ^[a-z0-9][a-z0-9-_]{0,31}$ each."),
             new ActionParameter("usage_hint", "String", "Optional guidance for future use; max 200 chars."),
             new ActionParameter("scope", "String", "Exactly workspace or global.")]),
        new("update", "Edit a curated memory under compare-and-swap.",
            "Requires id (GUID) and expected_version (integer >= 1), plus at least one delta among "
            + "content / category / tags / usage_hint. A stale expected_version fails with VersionConflict "
            + "naming the current version. Output: '[memories] updated <first8> v<n>'.",
            [new ActionParameter("id", "String", "GUID of the memory to edit."),
             new ActionParameter("expected_version", "Integer", "The version the caller believes is stored; integer >= 1."),
             new ActionParameter("content", "String", "Replacement content; trimmed non-empty, max 4000 chars."),
             new ActionParameter("category", "String", "Exact-lowercase replacement category."),
             new ActionParameter("tags", "String[]", "Replacement tag set; same rules as add."),
             new ActionParameter("usage_hint", "String", "Replacement hint; max 200 chars.")]),
        new("remove", "Delete a curated memory.",
            "Requires id (GUID) and confirm exactly boolean true. Unknown ids fail with MemoryNotFound. "
            + "Output: '[memories] removed <first8>'.",
            [new ActionParameter("id", "String", "GUID of the memory to delete."),
             new ActionParameter("confirm", "Boolean", "Must be exactly true; anything else fails RemoveNotConfirmed.")]),
    ];

    public async Task<CapabilityInvocationResult> InvokeAsync(
        string actionName, string jsonArguments, CancellationToken ct = default)
    {
        try
        {
            return actionName switch
            {
                "search" => await SearchAsync(jsonArguments),
                "add" => await AddAsync(jsonArguments),
                "update" => await UpdateAsync(jsonArguments),
                "remove" => await RemoveAsync(jsonArguments),
                _ => CapabilityInvocationResult.Fail($"Error [UnknownAction]: Unknown action: {actionName}."),
            };
        }
        catch (MemoryInputException ex)
        {
            return CapabilityInvocationResult.Fail($"Error [InvalidActionInput]: {ex.Message}");
        }
    }

    private async Task<CapabilityInvocationResult> SearchAsync(string json)
    {
        var args = ParseArgs(json, Allowed("query", "category", "tags", "scope", "limit"));

        // Same strict tag boundary as add/update: an invalid element is rejected
        // outright instead of being forwarded to become a silently-wrong filter.
        var tagFilters = OptStringArray(args, "tags");
        foreach (var tag in tagFilters)
            if (!CuratedMemorySpecifications.ValidTag(tag))
                return Fail(new Error("InvalidTag",
                    $"Invalid tag '{tag}': tags must match ^[a-z0-9][a-z0-9-_]{{0,31}}$."));

        var limit = OptInt(args, "limit") ?? DefaultLimit;
        if (limit < 1)
            return Fail(new Error("InvalidLimit", "'limit' must be an integer >= 1."));
        string? warning = null;
        if (limit > MaxLimit)
        {
            // The one sanctioned leniency: benign overshoot clamps to the cap and says so.
            limit = MaxLimit;
            warning = $"[warning] limit clamped to {MaxLimit}";
        }

        MemoryCategory? category = null;
        if (args.ContainsKey("category"))
        {
            var parsed = CuratedMemorySpecifications.ParseCategory(OptString(args, "category"));
            if (!parsed.IsSuccess) return Fail(parsed.Error!);
            category = parsed.Value;
        }

        MemoryScope? scope = null;
        if (args.ContainsKey("scope"))
        {
            var parsed = CuratedMemorySpecifications.ParseScope(OptString(args, "scope"));
            if (!parsed.IsSuccess) return Fail(parsed.Error!);
            scope = parsed.Value;
        }

        var search = await _store.SearchAsync(
            _workspaceId(), OptString(args, "query"), category, tagFilters, limit);
        if (!search.IsSuccess) return Fail(search.Error!);

        // The store ranks by visibility (global always; workspace only when it matches);
        // narrowing to a requested scope is a read-model concern on top of that ranking.
        var rows = scope is { } wanted
            ? search.Value!.Where(m => m.Scope == wanted).ToList()
            : search.Value!;
        var lines = new List<string> { $"[memories] {rows.Count} hit(s)" };
        foreach (var memory in rows)
        {
            lines.Add($"[mem] id={First8(memory.Id)} v{memory.Version} cat={Wire(memory.Category)}"
                      + $" scope={Wire(memory.Scope)} tags={string.Join(",", memory.Tags)}"
                      + $" :: {Truncate(memory.Content, ContentPreviewChars)}");
            if (!string.IsNullOrEmpty(memory.UsageHint))
                lines.Add($"     hint: {Truncate(memory.UsageHint, HintPreviewChars)}");
        }
        if (warning is not null)
            lines.Add(warning);

        return CapabilityInvocationResult.Ok(string.Join("\n", lines));
    }

    private async Task<CapabilityInvocationResult> AddAsync(string json)
    {
        var args = ParseArgs(json, Allowed("content", "category", "tags", "usage_hint", "scope"));

        if (ValidateContent(args) is { } contentError) return Fail(contentError);
        var content = OptString(args, "content")!.Trim();

        if (!args.TryGetValue("category", out var categoryElement)
            || categoryElement.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(categoryElement.GetString()))
            return Fail(new Error("MissingCategory",
                "'category' is required and must be exactly one of the five valid categories."));
        var category = CuratedMemorySpecifications.ParseCategory(categoryElement.GetString());
        if (!category.IsSuccess) return Fail(category.Error!);

        if (ValidateTags(args) is { } tagsError) return Fail(tagsError);
        var tags = args.ContainsKey("tags")
            ? CuratedMemorySpecifications.NormalizeTags(OptStringArray(args, "tags"))
            : [];

        string? usageHint = null;
        if (args.TryGetValue("usage_hint", out var hintElement))
        {
            if (hintElement.ValueKind != JsonValueKind.String || hintElement.GetString() is not { } hint)
                throw new MemoryInputException("'usage_hint' must be a string.");
            if (hint.Length > MaxHintChars)
                return Fail(new Error("HintTooLong",
                    $"Usage hint exceeds the {MaxHintChars}-character limit (actual: {hint.Length})."));
            usageHint = hint;
        }

        if (!args.TryGetValue("scope", out var scopeElement)
            || scopeElement.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(scopeElement.GetString()))
            return Fail(new Error("MissingScope",
                "'scope' is required and must be 'workspace' or 'global'."));
        var scope = CuratedMemorySpecifications.ParseScope(scopeElement.GetString());
        if (!scope.IsSuccess) return Fail(scope.Error!);

        var now = _clock();
        var memory = new CuratedMemory(
            Guid.NewGuid(),
            WorkspaceKey(scope.Value),
            category.Value,
            tags,
            content,
            usageHint,
            scope.Value,
            _provenance(),
            Version: 1,
            now,
            now);

        var added = await _store.AddAsync(memory);
        if (!added.IsSuccess) return Fail(added.Error!);
        _bumpWrites();
        return CapabilityInvocationResult.Ok(
            $"[memories] added {First8(added.Value!.Id)} v1"
            + $" (cat={Wire(added.Value.Category)} scope={Wire(added.Value.Scope)})");
    }

    private async Task<CapabilityInvocationResult> UpdateAsync(string json)
    {
        var args = ParseArgs(json, Allowed(
            "id", "expected_version", "content", "category", "tags", "usage_hint"));

        var (id, idError) = ParseId(args);
        if (idError is not null) return Fail(idError);

        var expectedVersion = OptInt(args, "expected_version");
        if (expectedVersion is null or < 1)
            return Fail(new Error("MissingVersion",
                "'expected_version' is required and must be an integer >= 1."));

        bool touchesContent = args.ContainsKey("content"), touchesCategory = args.ContainsKey("category"),
             touchesTags = args.ContainsKey("tags"), touchesHint = args.ContainsKey("usage_hint");
        if (!touchesContent && !touchesCategory && !touchesTags && !touchesHint)
            return Fail(new Error("NothingToUpdate",
                "Provide at least one of: content, category, tags, usage_hint."));

        if (touchesContent && ValidateContent(args) is { } contentError) return Fail(contentError);
        MemoryCategory? category = null;
        if (touchesCategory)
        {
            var parsed = CuratedMemorySpecifications.ParseCategory(OptString(args, "category"));
            if (!parsed.IsSuccess) return Fail(parsed.Error!);
            category = parsed.Value;
        }
        if (touchesTags && ValidateTags(args) is { } tagsError) return Fail(tagsError);
        IReadOnlyList<string>? tags = touchesTags
            ? CuratedMemorySpecifications.NormalizeTags(OptStringArray(args, "tags"))
            : null;
        string? usageHint = null;
        if (touchesHint)
        {
            if (args["usage_hint"].ValueKind != JsonValueKind.String
                || args["usage_hint"].GetString() is not { } hint)
                throw new MemoryInputException("'usage_hint' must be a string.");
            if (hint.Length > MaxHintChars)
                return Fail(new Error("HintTooLong",
                    $"Usage hint exceeds the {MaxHintChars}-character limit (actual: {hint.Length})."));
            usageHint = hint;
        }

        var fetched = await _store.GetAsync(id);
        if (!fetched.IsSuccess) return Fail(fetched.Error!);
        if (fetched.Value is not { } stored)
            return Fail(new Error(CuratedMemoryErrors.MemoryNotFound,
                $"No curated memory with id '{id}'."));

        // The caller's version belief is checked HERE: this surface always proposes
        // stored.Version + 1, so the store's CAS can only catch interleaved writers,
        // never a stale expected_version.
        if (expectedVersion.Value != stored.Version)
            return Fail(new Error(CuratedMemoryErrors.VersionConflict,
                $"current stored version is {stored.Version}."));

        var updated = stored with
        {
            Version = stored.Version + 1,
            UpdatedAt = _clock(),
            Content = touchesContent ? OptString(args, "content")!.Trim() : stored.Content,
            Category = category ?? stored.Category,
            Tags = tags ?? stored.Tags,
            UsageHint = touchesHint ? usageHint : stored.UsageHint,
        };

        var saved = await _store.UpdateAsync(updated);
        if (!saved.IsSuccess) return Fail(saved.Error!);
        return CapabilityInvocationResult.Ok(
            $"[memories] updated {First8(saved.Value!.Id)} v{saved.Value.Version}");
    }

    private async Task<CapabilityInvocationResult> RemoveAsync(string json)
    {
        var args = ParseArgs(json, Allowed("id", "confirm"));

        var (id, idError) = ParseId(args);
        if (idError is not null) return Fail(idError);

        if (!args.TryGetValue("confirm", out var confirm) || confirm.ValueKind != JsonValueKind.True)
            return Fail(new Error("RemoveNotConfirmed",
                "'confirm' must be exactly boolean true to remove a memory."));

        var deleted = await _store.DeleteAsync(id);
        if (!deleted.IsSuccess) return Fail(deleted.Error!);
        if (!deleted.Value)
            return Fail(new Error(CuratedMemoryErrors.MemoryNotFound,
                $"No curated memory with id '{id}'."));
        return CapabilityInvocationResult.Ok($"[memories] removed {First8(id)}");
    }

    // ---- shared validation ----

    /// <summary>Content rule shared by add and update: required, non-empty after trim,
    /// within the specification's character budget. Returns the violation, or null.</summary>
    private static Error? ValidateContent(Dictionary<string, JsonElement> args)
    {
        var content = OptString(args, "content");
        if (content is null || content.Trim().Length == 0)
            return new Error("MissingContent",
                "'content' is required and must be non-empty after trimming.");
        if (content.Trim().Length > CuratedMemorySpecifications.MaxContentChars)
            return new Error("ContentTooLong",
                $"Content exceeds the {CuratedMemorySpecifications.MaxContentChars}-character limit"
                + $" (actual: {content.Trim().Length}).");
        return null;
    }

    /// <summary>Tag rules shared by add and update: array of strings, at most MaxTags
    /// entries, each matching the specification's tag shape. Returns the violation, or null.</summary>
    private static Error? ValidateTags(Dictionary<string, JsonElement> args)
    {
        if (!args.ContainsKey("tags")) return null;
        var tags = OptStringArray(args, "tags");
        if (tags.Count > MaxTags)
            return new Error("TooManyTags",
                $"At most {MaxTags} tags are allowed (actual: {tags.Count}).");
        foreach (var tag in tags)
        {
            if (!CuratedMemorySpecifications.ValidTag(tag))
                return new Error("InvalidTag",
                    $"Invalid tag '{tag}': tags must match ^[a-z0-9][a-z0-9-_]{{0,31}}$.");
        }
        return null;
    }

    /// <summary>Parses the 'id' argument as a GUID, quoting the rejected input verbatim
    /// when absent, wrongly typed, or unparsable.</summary>
    private static (Guid Id, Error? Error) ParseId(Dictionary<string, JsonElement> args)
    {
        var raw = OptString(args, "id");
        if (raw is null)
            return (Guid.Empty, new Error("InvalidId", "'id' is required and must be a GUID."));
        return Guid.TryParse(raw, out var id)
            ? (id, null)
            : (Guid.Empty, new Error("InvalidId", $"'{raw}' is not a valid GUID."));
    }

    // ---- rendering helpers ----

    private static string Wire(MemoryCategory category) => category switch
    {
        MemoryCategory.Convention => "convention",
        MemoryCategory.Preference => "preference",
        MemoryCategory.Insight => "insight",
        MemoryCategory.Failure => "failure",
        MemoryCategory.Reference => "reference",
        _ => throw new InvalidOperationException($"Unhandled category: {category}"),
    };

    private static string Wire(MemoryScope scope) => scope switch
    {
        MemoryScope.Workspace => "workspace",
        MemoryScope.Global => "global",
        _ => throw new InvalidOperationException($"Unhandled scope: {scope}"),
    };

    /// <summary>Workspace rows are keyed by the service's injected workspace id; the
    ///     empty-string convention marks Global rows, which every workspace can see,
    ///     so no single workspace may claim them.</summary>
    private string WorkspaceKey(MemoryScope scope)
        => scope == MemoryScope.Workspace ? _workspaceId() : "";

    private static string First8(Guid id) => id.ToString("N")[..8];

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    private static CapabilityInvocationResult Fail(Error error)
        => CapabilityInvocationResult.Fail($"Error [{error.Code}]: {error.Message}");

    private static IReadOnlySet<string> Allowed(params string[] names) => new HashSet<string>(names, StringComparer.Ordinal);

    private static Dictionary<string, JsonElement> ParseArgs(string json, IReadOnlySet<string> allowed)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new MemoryInputException($"Arguments are not valid JSON: {ex.Message}");
        }
        if (root.ValueKind != JsonValueKind.Object)
            throw new MemoryInputException("Arguments must be a JSON object.");
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw new MemoryInputException($"Unknown parameter '{property.Name}'.");
            args[property.Name] = property.Value.Clone();
        }
        return args;
    }

    private static string? OptString(Dictionary<string, JsonElement> args, string name)
        => args.TryGetValue(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static int? OptInt(Dictionary<string, JsonElement> args, string name)
        => args.TryGetValue(name, out var element) && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out var value)
            ? value
            : null;

    private static IReadOnlyList<string> OptStringArray(Dictionary<string, JsonElement> args, string name)
    {
        if (!args.TryGetValue(name, out var element))
            return [];
        if (element.ValueKind != JsonValueKind.Array)
            throw new MemoryInputException($"'{name}' must be an array of strings.");
        var items = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new MemoryInputException($"'{name}' must contain only strings.");
            items.Add(item.GetString()!);
        }
        return items;
    }

    private sealed class MemoryInputException : Exception
    {
        public MemoryInputException(string message) : base(message) { }
    }
}
