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
public sealed class CuratedMemoryCapabilityProvider(
    ICuratedMemoryStore store,
    Func<string> workspaceId,
    Func<string?> provenance,
    Func<int> bumpWrites,
    Func<DateTimeOffset> clock) : ICapabilityProvider
{
  public const string ProviderId = "memories";

  private const int DefaultLimit = 20;
  private const int MaxLimit = 100;
  private const int MaxTags = 12;
  private const string Category = "category";
  private const string Scope = "scope";
  private const string Content = "content";
  private const string UsageHint = "usage_hint";
  private const int MaxHintChars = 200;
  private const int ContentPreviewChars = 120;
  private const int HintPreviewChars = 80;

  private readonly ICuratedMemoryStore _store = store ?? throw new ArgumentNullException(nameof(store));
  private readonly Func<string> _workspaceId = workspaceId ?? throw new ArgumentNullException(nameof(workspaceId));
  private readonly Func<string?> _provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
  private readonly Func<int> _bumpWrites = bumpWrites ?? throw new ArgumentNullException(nameof(bumpWrites));
  private readonly Func<DateTimeOffset> _clock = clock ?? throw new ArgumentNullException(nameof(clock));

  public string Id => ProviderId;

  public IReadOnlyList<ActionDescriptor> Actions { get; } =
  [
      new("search", "Search curated memories.",
            "Ranked hits: full-text match on query when given, otherwise newest-updated first. "
            + "All parameters are optional. Output format:\n"
            + "[memories] N hit(s)\n"
            + "[mem] id=<guid> v<n> cat=<category> scope=<scope> tags=t1,t2 :: <content <=120 chars>\n"
            + "     hint: <usage_hint <=80 chars>          (only when present)",
            [new ActionParameter("query", ActionParameterTypes.StringType, "Optional full-text query."),
             new ActionParameter(Category, ActionParameterTypes.StringType, "Optional exact-lowercase filter: convention | preference | insight | failure | reference."),
             new ActionParameter("tags", "String[]", "Optional tag filters; rows must carry all of them."),
             new ActionParameter(Scope, ActionParameterTypes.StringType, "Optional exact filter: workspace | global."),
             new ActionParameter("limit", "Integer", "Optional. Default 20; minimum 1; values above 100 clamp to 100 with a visible [warning] line.")]),
        new("add", "Store a durable curated memory.",
            "Requires content (trimmed non-empty, at most 4000 chars), category (exact-lowercase), "
            + "and scope (workspace | global). Optional tags (at most 12, each matching "
            + "^[a-z0-9][a-z0-9-_]{0,31}$, deduplicated) and usage_hint (at most 200 chars). The "
            + "session of record is captured automatically; a 'session' parameter is rejected. Output: "
            + "'[memories] added <guid> v1 (cat=<c> scope=<s>)'.",
            [new ActionParameter(Content, ActionParameterTypes.StringType, "The memory itself; trimmed non-empty, max 4000 chars."),
             new ActionParameter(Category, ActionParameterTypes.StringType, "Exactly one of: convention | preference | insight | failure | reference."),
             new ActionParameter("tags", "String[]", "Optional; max 12 tags, lowercase ^[a-z0-9][a-z0-9-_]{0,31}$ each."),
             new ActionParameter(UsageHint, ActionParameterTypes.StringType, "Optional guidance for future use; max 200 chars."),
             new ActionParameter(Scope, ActionParameterTypes.StringType, "Exactly workspace or global.")]),
        new("update", "Edit a curated memory under compare-and-swap.",
            "Requires id (GUID) and expected_version (integer >= 1), plus at least one delta among "
            + "content / category / tags / usage_hint. A stale expected_version fails with VersionConflict "
            + "naming the current version. Output: '[memories] updated <guid> v<n>'.",
            [new ActionParameter("id", ActionParameterTypes.StringType, "GUID of the memory to edit."),
             new ActionParameter("expected_version", "Integer", "The version the caller believes is stored; integer >= 1."),
             new ActionParameter(Content, ActionParameterTypes.StringType, "Replacement content; trimmed non-empty, max 4000 chars."),
             new ActionParameter(Category, ActionParameterTypes.StringType, "Exact-lowercase replacement category."),
             new ActionParameter("tags", "String[]", "Replacement tag set; same rules as add."),
             new ActionParameter(UsageHint, ActionParameterTypes.StringType, "Replacement hint; max 200 chars.")]),
        new("remove", "Delete a curated memory.",
            "Requires id (GUID) and confirm exactly boolean true. Unknown ids fail with MemoryNotFound. "
            + "Output: '[memories] removed <guid>'.",
            [new ActionParameter("id", ActionParameterTypes.StringType, "GUID of the memory to delete."),
             new ActionParameter("confirm", "Boolean", "Must be exactly true; anything else fails RemoveNotConfirmed.")]),
    ];

  public async Task<CapabilityInvocationResult> InvokeAsync(
      string actionName, string jsonArguments, CancellationToken ct = default)
  {
    try
    {
      return actionName switch
      {
        "search" => await SearchAsync(jsonArguments).ConfigureAwait(false),
        "add" => await AddAsync(jsonArguments).ConfigureAwait(false),
        "update" => await UpdateAsync(jsonArguments).ConfigureAwait(false),
        "remove" => await RemoveAsync(jsonArguments).ConfigureAwait(false),
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
    Dictionary<string, JsonElement> args = ParseArgs(json, Allowed("query", Category, "tags", Scope, "limit"));

    // Same strict tag boundary as add/update: an invalid element is rejected
    // outright instead of being forwarded to become a silently-wrong filter.
    IReadOnlyList<string> tagFilters = OptStringArray(args, "tags");
    DomainError? invalidTag = FirstInvalidTag(tagFilters);
    if (invalidTag is not null)
    {
      return Fail(invalidTag);
    }

    Result<(int Limit, string? Warning)> limit = ParseLimit(args);
    if (!limit.IsSuccess)
    {
      return Fail(limit.Error);
    }

    Result<MemoryCategory?> category = ParseCategoryFilter(args);
    if (!category.IsSuccess)
    {
      return Fail(category.Error);
    }

    Result<MemoryScope?> scope = ParseScopeFilter(args);
    if (!scope.IsSuccess)
    {
      return Fail(scope.Error);
    }

    Result<IReadOnlyList<CuratedMemory>> search = await _store.SearchAsync(
        _workspaceId(), OptString(args, "query"), category.Value, tagFilters, limit.Value.Limit).ConfigureAwait(false);
    if (!search.IsSuccess)
    {
      return Fail(search.Error);
    }

    // The store ranks by visibility: global always, workspace only when it matches —
    // narrowing to a requested scope is a read-model concern on top of that ranking.
    IReadOnlyList<CuratedMemory> rows = scope.Value is { } wanted
        ? [.. search.Value.Where(m => m.Scope == wanted)]
        : search.Value;
    return RenderHits(rows, limit.Value.Warning);
  }

  private static DomainError? FirstInvalidTag(IReadOnlyList<string> tags)
  {
    string? invalidTag = tags.FirstOrDefault(tag => !CuratedMemorySpecifications.ValidTag(tag));
    return invalidTag is null ? null : InvalidTagError(invalidTag);
  }

  private static DomainError InvalidTagError(string invalidTag) =>
      new("InvalidTag",
          $"Invalid tag '{invalidTag}': tags must match ^[a-z0-9][a-z0-9-_]{{0,31}}$.");

  /// <summary>Search limit: minimum 1; above the cap is the one sanctioned leniency —
  ///     clamped with a visible warning line.</summary>
  private static Result<(int Limit, string? Warning)> ParseLimit(Dictionary<string, JsonElement> args)
  {
    int limit = OptInt(args, "limit") ?? DefaultLimit;
    if (limit < 1)
    {
      return Result.Failure<(int, string?)>(new DomainError("InvalidLimit", "'limit' must be an integer >= 1."));
    }

    string? warning = null;
    if (limit > MaxLimit)
    {
      // The one sanctioned leniency: benign overshoot clamps to the cap and says so.
      limit = MaxLimit;
      warning = $"[warning] limit clamped to {MaxLimit}";
    }

    Result<(int Limit, string? Warning)> parsed = Result.Success((limit, warning));
    return parsed;
  }

  private static Result<MemoryCategory?> ParseCategoryFilter(Dictionary<string, JsonElement> args)
  {
    if (!args.ContainsKey(Category))
    {
      return Result.Success<MemoryCategory?>(null);
    }

    Result<MemoryCategory> parsed = CuratedMemorySpecifications.ParseCategory(OptString(args, Category));
    Result<MemoryCategory?> result = parsed.IsSuccess
      ? Result.Success<MemoryCategory?>(parsed.Value)
      : Result.Failure<MemoryCategory?>(parsed.Error);
    return result;
  }

  private static Result<MemoryScope?> ParseScopeFilter(Dictionary<string, JsonElement> args)
  {
    if (!args.ContainsKey(Scope))
    {
      return Result.Success<MemoryScope?>(null);
    }

    Result<MemoryScope> parsed = CuratedMemorySpecifications.ParseScope(OptString(args, Scope));
    Result<MemoryScope?> result = parsed.IsSuccess
      ? Result.Success<MemoryScope?>(parsed.Value)
      : Result.Failure<MemoryScope?>(parsed.Error);
    return result;
  }

  private static CapabilityInvocationResult RenderHits(IReadOnlyList<CuratedMemory> rows, string? warning)
  {
    List<string> lines = [$"[memories] {rows.Count} hit(s)"];
    foreach (CuratedMemory memory in rows)
    {
      lines.Add($"[mem] id={memory.Id} v{memory.Version} cat={Wire(memory.Category)}"
                + $" scope={Wire(memory.Scope)} tags={string.Join(",", memory.Tags)}"
                + $" :: {Truncate(memory.Content, ContentPreviewChars)}");
      if (!string.IsNullOrEmpty(memory.UsageHint))
      {
        lines.Add($"     hint: {Truncate(memory.UsageHint, HintPreviewChars)}");
      }
    }
    if (warning is not null)
    {
      lines.Add(warning);
    }

    return CapabilityInvocationResult.Ok(string.Join("\n", lines));
  }

  private async Task<CapabilityInvocationResult> AddAsync(string json)
  {
    Dictionary<string, JsonElement> args = ParseArgs(json, Allowed(Content, Category, "tags", UsageHint, Scope));

    if (ValidateContent(args) is { } contentError)
    {
      return Fail(contentError);
    }

    string content = OptString(args, Content)!.Trim();

    Result<MemoryCategory> category = ParseRequiredCategory(args);
    if (!category.IsSuccess)
    {
      return Fail(category.Error);
    }

    if (ValidateTags(args) is { } tagsError)
    {
      return Fail(tagsError);
    }

    IReadOnlyList<string> tags = args.ContainsKey("tags")
        ? CuratedMemorySpecifications.NormalizeTags(OptStringArray(args, "tags"))
        : [];

    Result<string?> usageHint = ParseOptionalHint(args);
    if (!usageHint.IsSuccess)
    {
      return Fail(usageHint.Error);
    }

    Result<MemoryScope> scope = ParseRequiredScope(args);
    if (!scope.IsSuccess)
    {
      return Fail(scope.Error);
    }

    CuratedMemory memory = BuildMemory(category.Value, scope.Value, tags, content, usageHint.Value);
    Result<CuratedMemory> added = await _store.AddAsync(memory).ConfigureAwait(false);
    if (!added.IsSuccess)
    {
      return Fail(added.Error);
    }

    _ = _bumpWrites();
    CapabilityInvocationResult ok = CapabilityInvocationResult.Ok(
        $"[memories] added {added.Value.Id} v1"
        + $" (cat={Wire(added.Value.Category)} scope={Wire(added.Value.Scope)})");
    return ok;
  }

  private static Result<MemoryCategory> ParseRequiredCategory(Dictionary<string, JsonElement> args)
  {
    Result<MemoryCategory> category = args.TryGetValue(Category, out JsonElement categoryElement)
        && categoryElement.ValueKind == JsonValueKind.String
        && !string.IsNullOrEmpty(categoryElement.GetString())
      ? CuratedMemorySpecifications.ParseCategory(categoryElement.GetString())
      : Result.Failure<MemoryCategory>(new DomainError("MissingCategory",
          "'category' is required and must be exactly one of the five valid categories."));
    return category;
  }

  private static Result<MemoryScope> ParseRequiredScope(Dictionary<string, JsonElement> args)
  {
    Result<MemoryScope> scope = args.TryGetValue(Scope, out JsonElement scopeElement)
        && scopeElement.ValueKind == JsonValueKind.String
        && !string.IsNullOrEmpty(scopeElement.GetString())
      ? CuratedMemorySpecifications.ParseScope(scopeElement.GetString())
      : Result.Failure<MemoryScope>(new DomainError("MissingScope",
          "'scope' is required and must be 'workspace' or 'global'."));
    return scope;
  }

  /// <summary>Optional usage_hint: wrong type is malformed input (thrown), over-budget
  ///     is a typed failure; null when absent.</summary>
  private static Result<string?> ParseOptionalHint(Dictionary<string, JsonElement> args)
  {
    if (!args.TryGetValue(UsageHint, out JsonElement hintElement))
    {
      return Result.Success<string?>(null);
    }

    if (hintElement.ValueKind != JsonValueKind.String || hintElement.GetString() is not { } hint)
    {
      throw new MemoryInputException("'usage_hint' must be a string.");
    }

    Result<string?> usageHint = hint.Length > MaxHintChars
      ? Result.Failure<string?>(new DomainError("HintTooLong",
          $"Usage hint exceeds the {MaxHintChars}-character limit (actual: {hint.Length})."))
      : Result.Success<string?>(hint);
    return usageHint;
  }

  private CuratedMemory BuildMemory(MemoryCategory category, MemoryScope scope,
      IReadOnlyList<string> tags, string content, string? usageHint)
  {
    DateTimeOffset now = _clock();
    CuratedMemory memory = new(
        Guid.NewGuid(),
        WorkspaceKey(scope),
        category,
        tags,
        content,
        usageHint,
        scope,
        _provenance(),
        Version: 1,
        now,
        now);
    return memory;
  }

  private async Task<CapabilityInvocationResult> UpdateAsync(string json)
  {
    Dictionary<string, JsonElement> args = ParseArgs(json, Allowed(
        "id", "expected_version", Content, Category, "tags", UsageHint));

    (Guid id, DomainError? idError) = ParseId(args);
    if (idError is not null)
    {
      return Fail(idError);
    }

    Result<int> expectedVersion = ParseExpectedVersion(args);
    if (!expectedVersion.IsSuccess)
    {
      return Fail(expectedVersion.Error);
    }

    Result<MemoryDelta> delta = ParseDelta(args);
    if (!delta.IsSuccess)
    {
      return Fail(delta.Error);
    }

    Result<CuratedMemory> stored = await FetchForUpdateAsync(id, expectedVersion.Value).ConfigureAwait(false);
    if (!stored.IsSuccess)
    {
      return Fail(stored.Error);
    }

    CuratedMemory updated = BuildUpdate(stored.Value, args, delta.Value);
    Result<CuratedMemory> saved = await _store.UpdateAsync(updated).ConfigureAwait(false);
    CapabilityInvocationResult result = saved.IsSuccess
      ? CapabilityInvocationResult.Ok(
        $"[memories] updated {saved.Value.Id} v{saved.Value.Version}")
      : Fail(saved.Error);
    return result;
  }

  /// <summary>The compare-and-swap belief: required, integer >= 1.</summary>
  private static Result<int> ParseExpectedVersion(Dictionary<string, JsonElement> args)
  {
    int? expectedVersion = OptInt(args, "expected_version");
    Result<int> version = expectedVersion is >= 1
      ? Result.Success(expectedVersion.Value)
      : Result.Failure<int>(new DomainError("MissingVersion",
          "'expected_version' is required and must be an integer >= 1."));
    return version;
  }

  /// <summary>Validates the present delta fields (at least one required), in the
  ///     documented order: content, category, tags, usage_hint. Absent fields stay null.</summary>
  private static Result<MemoryDelta> ParseDelta(Dictionary<string, JsonElement> args)
  {
    bool touchesContent = args.ContainsKey(Content), touchesCategory = args.ContainsKey(Category),
         touchesTags = args.ContainsKey("tags"), touchesHint = args.ContainsKey(UsageHint);
    if (!touchesContent && !touchesCategory && !touchesTags && !touchesHint)
    {
      return Result.Failure<MemoryDelta>(new DomainError("NothingToUpdate",
          "Provide at least one of: content, category, tags, usage_hint."));
    }

    if (touchesContent && ValidateContent(args) is { } contentError)
    {
      return Result.Failure<MemoryDelta>(contentError);
    }

    Result<MemoryCategory?> category = ParseCategoryUpdate(args, touchesCategory);
    if (!category.IsSuccess)
    {
      return Result.Failure<MemoryDelta>(category.Error);
    }

    if (touchesTags && ValidateTags(args) is { } tagsError)
    {
      return Result.Failure<MemoryDelta>(tagsError);
    }

    Result<string?> usageHint = ParseOptionalHint(args);
    if (!usageHint.IsSuccess)
    {
      return Result.Failure<MemoryDelta>(usageHint.Error);
    }

    IReadOnlyList<string>? tags = touchesTags
        ? CuratedMemorySpecifications.NormalizeTags(OptStringArray(args, "tags"))
        : null;
    MemoryDelta delta = new(category.Value, tags, usageHint.Value);
    return Result.Success(delta);
  }

  private static Result<MemoryCategory?> ParseCategoryUpdate(Dictionary<string, JsonElement> args, bool touchesCategory)
  {
    if (!touchesCategory)
    {
      return Result.Success<MemoryCategory?>(null);
    }

    Result<MemoryCategory> parsed = CuratedMemorySpecifications.ParseCategory(OptString(args, Category));
    Result<MemoryCategory?> result = parsed.IsSuccess
      ? Result.Success<MemoryCategory?>(parsed.Value)
      : Result.Failure<MemoryCategory?>(parsed.Error);
    return result;
  }

  /// <summary>Loads the stored row and applies the version-belief check here: this
  ///     surface always proposes stored.Version + 1, so the store's CAS can only
  ///     catch interleaved writers, never a stale expected_version.</summary>
  private async Task<Result<CuratedMemory>> FetchForUpdateAsync(Guid id, int expectedVersion)
  {
    Result<CuratedMemory?> fetched = await _store.GetAsync(id).ConfigureAwait(false);
    if (!fetched.IsSuccess)
    {
      return Result.Failure<CuratedMemory>(fetched.Error);
    }

    if (fetched.Value is not { } stored)
    {
      return Result.Failure<CuratedMemory>(new DomainError(CuratedMemoryErrors.MemoryNotFound,
          $"No curated memory with id '{id}'."));
    }

    Result<CuratedMemory> versionOk = expectedVersion == stored.Version
      ? Result.Success(stored)
      : Result.Failure<CuratedMemory>(new DomainError(CuratedMemoryErrors.VersionConflict,
          $"current stored version is {stored.Version}."));
    return versionOk;
  }

  /// <summary>The proposed row: untouched fields carry over, touched ones overlay.
  ///     Category/tags overlay through null-coalescing; content and hint re-check
  ///     presence so an explicit empty delta keeps its own value.</summary>
  private CuratedMemory BuildUpdate(CuratedMemory stored, Dictionary<string, JsonElement> args, MemoryDelta delta)
  {
    CuratedMemory updated = stored with
    {
      Version = stored.Version + 1,
      UpdatedAt = _clock(),
      Content = args.ContainsKey(Content) ? OptString(args, Content)!.Trim() : stored.Content,
      Category = delta.NewCategory ?? stored.Category,
      Tags = delta.Tags ?? stored.Tags,
      UsageHint = args.ContainsKey(UsageHint) ? delta.NewUsageHint : stored.UsageHint,
    };
    return updated;
  }

  /// <summary>The validated update payload: null fields mean "not touched". Property
  /// names carry the New prefix so they cannot shadow the JSON-key consts above.</summary>
  private sealed record MemoryDelta(MemoryCategory? NewCategory, IReadOnlyList<string>? Tags, string? NewUsageHint);

  private async Task<CapabilityInvocationResult> RemoveAsync(string json)
  {
    Dictionary<string, JsonElement> args = ParseArgs(json, Allowed("id", "confirm"));

    (Guid id, DomainError? idError) = ParseId(args);
    if (idError is not null)
    {
      return Fail(idError);
    }

    if (!args.TryGetValue("confirm", out JsonElement confirm) || confirm.ValueKind != JsonValueKind.True)
    {
      return Fail(new DomainError("RemoveNotConfirmed",
          "'confirm' must be exactly boolean true to remove a memory."));
    }

    Result<bool> deleted = await _store.DeleteAsync(id).ConfigureAwait(false);
    if (!deleted.IsSuccess)
    {
      return Fail(deleted.Error);
    }

    if (!deleted.Value)
    {
      return Fail(new DomainError(CuratedMemoryErrors.MemoryNotFound,
          $"No curated memory with id '{id}'."));
    }

    CapabilityInvocationResult removed = CapabilityInvocationResult.Ok($"[memories] removed {id}");
    return removed;
  }

  // ---- shared validation ----

  /// <summary>Content rule shared by add and update: required, non-empty after trim,
  /// within the specification's character budget. Returns the violation, or null.</summary>
  private static DomainError? ValidateContent(Dictionary<string, JsonElement> args)
  {
    string? content = OptString(args, Content);
    if (content is null || content.Trim().Length == 0)
    {
      return new DomainError("MissingContent",
          "'content' is required and must be non-empty after trimming.");
    }

    string trimmed = content.Trim();
    return trimmed.Length > CuratedMemorySpecifications.MaxContentChars
        ? new DomainError("ContentTooLong",
            $"Content exceeds the {CuratedMemorySpecifications.MaxContentChars}-character limit"
            + $" (actual: {trimmed.Length}).")
        : null;
  }

  /// <summary>Tag rules shared by add and update: array of strings, at most MaxTags
  /// entries, each matching the specification's tag shape. Returns the violation, or null.</summary>
  private static DomainError? ValidateTags(Dictionary<string, JsonElement> args)
  {
    if (!args.ContainsKey("tags"))
    {
      return null;
    }

    List<string> tags = OptStringArray(args, "tags");
    if (tags.Count > MaxTags)
    {
      return new DomainError("TooManyTags",
          $"At most {MaxTags} tags are allowed (actual: {tags.Count}).");
    }

    string? invalidTag = tags.FirstOrDefault(tag => !CuratedMemorySpecifications.ValidTag(tag));
    return invalidTag is null ? null : InvalidTagError(invalidTag);
  }

  /// <summary>Parses the 'id' argument as a GUID, quoting the rejected input verbatim
  /// when absent, wrongly typed, or unparsable.</summary>
  private static (Guid Id, DomainError? DomainError) ParseId(Dictionary<string, JsonElement> args)
  {
    string? raw = OptString(args, "id");
    if (raw is null)
    {
      return (Guid.Empty, new DomainError("InvalidId", "'id' is required and must be a GUID."));
    }

    return Guid.TryParse(raw, out Guid id)
        ? (id, null)
        : (Guid.Empty, new DomainError("InvalidId", $"'{raw}' is not a valid GUID."));
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


  private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

  private static CapabilityInvocationResult Fail(DomainError error)
      => CapabilityInvocationResult.Fail($"Error [{error.Code}]: {error.Message}");

  private static HashSet<string> Allowed(params string[] names) => new(names, StringComparer.Ordinal);

  private static Dictionary<string, JsonElement> ParseArgs(string json, HashSet<string> allowed)
  {
    JsonElement root;
    try
    {
      using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
      root = doc.RootElement.Clone();
    }
    catch (JsonException ex)
    {
      throw new MemoryInputException($"Arguments are not valid JSON: {ex.Message}");
    }
    if (root.ValueKind != JsonValueKind.Object)
    {
      throw new MemoryInputException("Arguments must be a JSON object.");
    }

    Dictionary<string, JsonElement> args = new(StringComparer.Ordinal);
    foreach (JsonProperty property in root.EnumerateObject())
    {
      if (!allowed.Contains(property.Name))
      {
        throw new MemoryInputException($"Unknown parameter '{property.Name}'.");
      }

      args[property.Name] = property.Value.Clone();
    }
    return args;
  }

  private static string? OptString(Dictionary<string, JsonElement> args, string name)
      => args.TryGetValue(name, out JsonElement element) && element.ValueKind == JsonValueKind.String
          ? element.GetString()
          : null;

  private static int? OptInt(Dictionary<string, JsonElement> args, string name)
      => args.TryGetValue(name, out JsonElement element) && element.ValueKind == JsonValueKind.Number
          && element.TryGetInt32(out int value)
          ? value
          : null;

  private static List<string> OptStringArray(Dictionary<string, JsonElement> args, string name)
  {
    if (!args.TryGetValue(name, out JsonElement element))
    {
      return [];
    }

    if (element.ValueKind != JsonValueKind.Array)
    {
      throw new MemoryInputException($"'{name}' must be an array of strings.");
    }

    List<string> items = [];
    foreach (JsonElement item in element.EnumerateArray())
    {
      if (item.ValueKind != JsonValueKind.String)
      {
        throw new MemoryInputException($"'{name}' must contain only strings.");
      }

      items.Add(item.GetString()!);
    }
    return items;
  }
}

/// <summary>Signals malformed capability arguments during parsing. Public only because
///     CA1064 forbids non-public exception types; never escapes the provider - each
///     action catches it and renders the message as a typed tool error.</summary>
public sealed class MemoryInputException : Exception
{
  public MemoryInputException() : base("Invalid capability arguments.") { }
  public MemoryInputException(string message) : base(message) { }
  public MemoryInputException(string message, Exception innerException) : base(message, innerException) { }
}
