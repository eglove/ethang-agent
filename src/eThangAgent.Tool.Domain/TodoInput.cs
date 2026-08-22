using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public enum TodoAction { Add, Update, Complete, Remove, List, Clear }

public sealed record TodoInput(TodoAction Action, int? Id, string? Description, TodoStatus? Status)
{
    public static Result<TodoInput> Create(string jsonArguments)
    {
        JsonElement json;
        try
        {
            using var doc = JsonDocument.Parse(jsonArguments);
            json = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return Fail(new Error("InvalidJsonArguments",
                $"Arguments are not valid JSON: {ex.Message}"));
        }
        if (json.ValueKind != JsonValueKind.Object)
            return Fail(new Error("InvalidJsonArguments", "Arguments must be a JSON object."));

        var known = new HashSet<string>(
            ["action", "id", "description", "status", "confirm"], StringComparer.Ordinal);
        var unknown = json.EnumerateObject()
            .Where(p => !known.Contains(p.Name))
            .Select(p => p.Name)
            .ToList();
        if (unknown.Count > 0)
            return Fail(new Error("UnknownParameter",
                $"Unknown parameter(s): {string.Join(", ", unknown)}. " +
                "Allowed: action, id, description, status, confirm."));

        if (!json.TryGetProperty("action", out var actionEl)) return MissingAction();
        if (actionEl.ValueKind != JsonValueKind.String)
            return Fail(new Error("InvalidParameterType",
                $"'action' must be a string, but got {actionEl.ValueKind}."));
        var actionText = actionEl.GetString()!;
        var action = actionText switch
        {
            nameof(TodoAction.Add) => TodoAction.Add,
            nameof(TodoAction.Update) => TodoAction.Update,
            nameof(TodoAction.Complete) => TodoAction.Complete,
            nameof(TodoAction.Remove) => TodoAction.Remove,
            nameof(TodoAction.List) => TodoAction.List,
            nameof(TodoAction.Clear) => TodoAction.Clear,
            _ => (TodoAction?)null,
        };
        if (action is null)
            return Fail(new Error("InvalidParameterValue",
                $"'action' must be exactly one of Add, Update, Complete, Remove, List, Clear " +
                $"(case-sensitive), but got '{actionText}'."));

        // id: optional at this stage; required per action below. Type and range are
        // checked here so every action shares one rule.
        int? id = null;
        if (json.TryGetProperty("id", out var idEl))
        {
            if (idEl.ValueKind != JsonValueKind.Number || !idEl.TryGetInt32(out var parsedId))
                return Fail(new Error("InvalidParameterType",
                    $"'id' must be an integer, but got {idEl.ValueKind}."));
            if (parsedId <= 0)
                return Fail(new Error("InvalidParameterValue",
                    $"'id' must be a positive integer, but got {parsedId}."));
            id = parsedId;
        }

        string? description = null;
        if (json.TryGetProperty("description", out var descEl))
        {
            if (descEl.ValueKind != JsonValueKind.String)
                return Fail(new Error("InvalidParameterType",
                    $"'description' must be a string, but got {descEl.ValueKind}."));
            description = descEl.GetString()!;
        }

        TodoStatus? status = null;
        if (json.TryGetProperty("status", out var statusEl))
        {
            if (statusEl.ValueKind != JsonValueKind.String)
                return Fail(new Error("InvalidParameterType",
                    $"'status' must be a string, but got {statusEl.ValueKind}."));
            var statusText = statusEl.GetString()!;
            status = statusText switch
            {
                nameof(TodoStatus.Pending) => TodoStatus.Pending,
                nameof(TodoStatus.InProgress) => TodoStatus.InProgress,
                nameof(TodoStatus.Completed) => TodoStatus.Completed,
                _ => null,
            };
            if (status is null)
                return Fail(new Error("InvalidParameterValue",
                    $"'status' must be exactly one of Pending, InProgress, Completed " +
                    $"(case-sensitive), but got '{statusText}'."));
        }

        switch (action)
        {
            case TodoAction.Add:
                if (description is null)
                    return Fail(new Error("MissingParameter",
                        "Missing required parameter 'description'. Add requires a non-empty description."));
                if (description.Length == 0)
                    return Fail(new Error("InvalidParameterValue",
                        "'description' must be a non-empty string."));
                break;

            case TodoAction.Update:
                if (id is null) return MissingId();
                if (description is not null && description.Length == 0)
                    return Fail(new Error("InvalidParameterValue",
                        "'description' must be a non-empty string."));
                if (description is null && status is null)
                    return Fail(new Error("InvalidParameterValue",
                        "Update changes nothing: provide at least one of 'description' or 'status'."));
                break;

            case TodoAction.Complete:
            case TodoAction.Remove:
                if (id is null) return MissingId();
                break;

            case TodoAction.Clear:
                // Clearing is destructive; anything other than an exact boolean true
                // (including a missing confirm) stops at the gate.
                if (!json.TryGetProperty("confirm", out var confirmEl) ||
                    confirmEl.ValueKind != JsonValueKind.True)
                    return Fail(new Error("InvalidParameterValue",
                        "'confirm' is required to clear the todo list and must be exactly the " +
                        "boolean true. Re-issue with \"confirm\": true to proceed."));
                break;
        }

        return Result<TodoInput>.Success(new(action.Value, id, description, status));
    }

    private static Result<TodoInput> MissingAction() =>
        Result<TodoInput>.Failure(new Error("MissingParameter",
            "Missing required parameter 'action'. This tool requires action " +
            "(Add, Update, Complete, Remove, List, or Clear)."));

    private static Result<TodoInput> MissingId() =>
        Result<TodoInput>.Failure(new Error("MissingParameter",
            "Missing required parameter 'id'. Update, Complete, and Remove require the id of the target item."));

    private static Result<TodoInput> Fail(Error err) =>
        Result<TodoInput>.Failure(err);
}
