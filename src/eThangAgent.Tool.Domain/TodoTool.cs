using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Port the Tool Domain owns for durable key-value storage behind the todo
///     tool. The composition root adapts the State Domain's IStateService to it — the
///     domain depends only on this contract, never on another context's types, so any
///     store that can express get/CAS-write can be wired in without domain changes.</summary>
public interface ITodoListStore
{
    /// <summary>Reads the raw value stored under <paramref name="key"/>, or fails with
    ///     KeyNotFound when the key does not exist yet.</summary>
    Task<Result<string>> GetValueAsync(string key, CancellationToken ct = default);

    /// <summary>CAS write: when <paramref name="expectedVersion"/> is supplied it must
    ///     match the stored version (VersionConflict otherwise); null creates the key
    ///     or overwrites it. Returns the new version.</summary>
    Task<Result<int>> WriteValueAsync(string key, string value, int? expectedVersion,
        CancellationToken ct = default);
}

public sealed class TodoTool : ITool
{
    public const string StoreKey = "todo/list";

    private readonly ITodoListStore _store;

    // The store version this instance last wrote. Reads return only the value, so the
    // version memo is what makes CAS real within a process lifetime: the first touch of
    // a key writes with null (create/upsert), every later write CASes against the memo,
    // and a concurrent writer turns that into a retryable VersionConflict. Rare in a
    // single-agent CLI; honest fail-closed either way.
    private int? _lastWrittenVersion;

    public ToolDefinition Definition { get; } = new(
        "todo",
        "Track the workspace task list persisted in durable state. action is exactly Add, " +
        "Update, Complete, Remove, List, or Clear (case-sensitive). Add requires a non-empty " +
        "description; new items get the next free id and start Pending. Update requires id plus " +
        "at least one of description or status; status is exactly Pending, InProgress, or " +
        "Completed. Complete requires id and marks the item Completed (repeating it on an " +
        "already-completed item succeeds). Remove requires id. List takes no other parameters " +
        "and prints '[todo: N open / M total]' followed by one '#id [status] description' line " +
        "per item, or '[todo: empty]' when none. Clear requires confirm to be exactly the " +
        "boolean true and empties the list. Mutations print one annotation line: " +
        "'[todo] added #3', '[todo] updated #3', '[todo] completed #3', '[todo] removed #3', " +
        "or '[todo] cleared'. Unknown ids fail with TodoNotFound. A concurrent modification " +
        "fails with VersionConflict \u2014 re-issue the same call to retry. Errors begin with " +
        "`Error [Code]:`.",
        [
            new ToolParameter("action", ToolParameterType.String,
                "Exactly Add, Update, Complete, Remove, List, or Clear (case-sensitive)."),
            new ToolParameter("id", ToolParameterType.Integer,
                "Update, Complete, and Remove: id of the target item (positive integer)."),
            new ToolParameter("description", ToolParameterType.String,
                "Add: required non-empty task text. Update: optional replacement text."),
            new ToolParameter("status", ToolParameterType.String,
                "Update only: exactly Pending, InProgress, or Completed (case-sensitive)."),
            new ToolParameter("confirm", ToolParameterType.Boolean,
                "Clear only: must be exactly true; clearing empties the list."),
        ]);

    public TodoTool(ITodoListStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
        var parsed = TodoInput.Create(input.JsonArguments);
        if (!parsed.IsSuccess)
            return Err(parsed.Error!);

        var read = await _store.GetValueAsync(StoreKey, ct);
        IReadOnlyList<TodoItem> doc;
        if (read.IsSuccess)
        {
            var parsedDoc = TodoDocument.Parse(read.Value!);
            if (!parsedDoc.IsSuccess)
                return Corrupt(parsedDoc.Error!);
            doc = parsedDoc.Value!;
        }
        // A missing key is an empty document, not an error.
        else if (read.Error!.Code == "KeyNotFound")
        {
            doc = TodoDocument.Empty;
        }
        else
        {
            return Err(read.Error);
        }

        return parsed.Value!.Action switch
        {
            TodoAction.List => List(doc),
            TodoAction.Add => await AddAsync(parsed.Value, doc, ct),
            TodoAction.Update => await UpdateAsync(parsed.Value, doc, ct),
            TodoAction.Complete => await CompleteAsync(parsed.Value, doc, ct),
            TodoAction.Remove => await RemoveAsync(parsed.Value, doc, ct),
            _ => await ClearAsync(ct),
        };
    }

    private static ToolResult List(IReadOnlyList<TodoItem> doc)
    {
        if (doc.Count == 0)
            return new ToolResult("[todo: empty]", false);

        var open = doc.Count(i => i.Status != TodoStatus.Completed);
        var lines = new List<string>(doc.Count + 1)
        {
            $"[todo: {open} open / {doc.Count} total]",
        };
        lines.AddRange(doc.Select(i => $"#{i.Id} [{TodoDocument.StatusText(i.Status)}] {i.Description}"));
        return new ToolResult(string.Join("\n", lines), false);
    }

    private async Task<ToolResult> AddAsync(TodoInput input, IReadOnlyList<TodoItem> doc,
        CancellationToken ct)
    {
        var nextId = doc.Count == 0 ? 1 : doc.Max(i => i.Id) + 1;
        var items = doc.Append(new TodoItem(nextId, input.Description!, TodoStatus.Pending)).ToList();
        var failed = await PersistAsync(items, ct);
        return failed ?? new ToolResult($"[todo] added #{nextId}", false);
    }

    private async Task<ToolResult> UpdateAsync(TodoInput input, IReadOnlyList<TodoItem> doc,
        CancellationToken ct)
    {
        var id = input.Id!.Value;
        var existing = doc.FirstOrDefault(i => i.Id == id);
        if (existing is null)
            return NotFound(id);

        var items = doc.Select(i => i.Id == id
            ? i with
            {
                Description = input.Description ?? i.Description,
                Status = input.Status ?? i.Status,
            }
            : i).ToList();
        var failed = await PersistAsync(items, ct);
        return failed ?? new ToolResult($"[todo] updated #{id}", false);
    }

    private async Task<ToolResult> CompleteAsync(TodoInput input, IReadOnlyList<TodoItem> doc,
        CancellationToken ct)
    {
        var id = input.Id!.Value;
        var existing = doc.FirstOrDefault(i => i.Id == id);
        if (existing is null)
            return NotFound(id);

        // Idempotent on an already-completed item: same document, still a success.
        var items = doc.Select(i => i.Id == id
            ? i with { Status = TodoStatus.Completed }
            : i).ToList();
        var failed = await PersistAsync(items, ct);
        return failed ?? new ToolResult($"[todo] completed #{id}", false);
    }

    private async Task<ToolResult> RemoveAsync(TodoInput input, IReadOnlyList<TodoItem> doc,
        CancellationToken ct)
    {
        var id = input.Id!.Value;
        if (doc.All(i => i.Id != id))
            return NotFound(id);

        var items = doc.Where(i => i.Id != id).ToList();
        var failed = await PersistAsync(items, ct);
        return failed ?? new ToolResult($"[todo] removed #{id}", false);
    }

    private async Task<ToolResult> ClearAsync(CancellationToken ct)
    {
        var failed = await PersistAsync(TodoDocument.Empty, ct);
        return failed ?? new ToolResult("[todo] cleared", false);
    }

    /// <summary>Persists the mutated document with CAS. Returns the error result on
    ///     failure, or null when the write landed (the version memo advances).</summary>
    private async Task<ToolResult?> PersistAsync(IReadOnlyList<TodoItem> items, CancellationToken ct)
    {
        var saved = await _store.WriteValueAsync(StoreKey, TodoDocument.Serialize(items),
            _lastWrittenVersion, ct);
        if (!saved.IsSuccess)
        {
            var error = saved.Error!;
            return new ToolResult(error.Code == "VersionConflict"
                ? $"Error [VersionConflict]: {error.Message} The todo list changed concurrently " +
                  "before this write landed. Re-issue the same call to retry against the latest state."
                : $"Error [{error.Code}]: {error.Message}",
                true);
        }

        _lastWrittenVersion = saved.Value;
        return null;
    }

    private static ToolResult NotFound(int id) =>
        Err(new Error("TodoNotFound",
            $"No todo item with id {id}. Use action List to see the current ids."));

    private static ToolResult Corrupt(Error error) =>
        new($"Error [{error.Code}]: {error.Message} Key '{StoreKey}' is left unchanged; " +
            "repair or clear it before issuing further calls.",
            true);

    private static ToolResult Err(Error error) => new($"Error [{error.Code}]: {error.Message}", true);
}
