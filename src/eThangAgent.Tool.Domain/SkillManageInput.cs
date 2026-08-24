using System.Text.Json;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.ToolDomain;

public enum SkillManageAction { Create, Update, Delete }

public sealed record SkillManageInput(
    SkillManageAction Action,
    string Name,
    string? Description,
    string? Body,
    string? ProvenanceSession)
{
    public static Result<SkillManageInput> Create(string jsonArguments)
    {
        var baseParse = ToolArguments.ParseObject(jsonArguments);
        if (!baseParse.IsSuccess)
            return Fail(baseParse.Error!);
        var json = baseParse.Value;

        var known = new HashSet<string>(
            ["action", "name", "description", "body", "provenanceSession", "confirm", ToolTimeout.ParameterName],
            StringComparer.Ordinal);
        var unknown = json.EnumerateObject()
            .Where(p => !known.Contains(p.Name))
            .Select(p => p.Name)
            .ToList();
        if (unknown.Count > 0)
            return Fail(new Error("UnknownParameter",
                $"Unknown parameter(s): {string.Join(", ", unknown)}. " +
                $"Allowed: action, name, description, body, provenanceSession, confirm, {ToolTimeout.ParameterName}."));

        if (!json.TryGetProperty("action", out var actionEl)) return MissingAction();
        if (actionEl.ValueKind != JsonValueKind.String)
            return Fail(new Error("InvalidParameterType",
                $"'action' must be a string, but got {actionEl.ValueKind}."));
        var actionText = actionEl.GetString()!;
        var action = actionText switch
        {
            nameof(SkillManageAction.Create) => SkillManageAction.Create,
            nameof(SkillManageAction.Update) => SkillManageAction.Update,
            nameof(SkillManageAction.Delete) => SkillManageAction.Delete,
            _ => (SkillManageAction?)null,
        };
        if (action is null)
            return Fail(new Error("InvalidParameterValue",
                $"'action' must be exactly one of Create, Update, Delete (case-sensitive), but got '{actionText}'."));

        if (!json.TryGetProperty("name", out var nameEl)) return MissingName();
        if (nameEl.ValueKind != JsonValueKind.String)
            return Fail(new Error("InvalidParameterType",
                $"'name' must be a string, but got {nameEl.ValueKind}."));
        var name = nameEl.GetString()!;
        if (name.Length == 0)
            return Fail(new Error("InvalidParameterValue", "'name' must be a non-empty string."));
        if (!SkillSpecifications.ValidName.IsMatch(name))
            return Fail(new Error("InvalidParameterValue",
                "'name' must be a valid skill name: lowercase letters, digits, and hyphens only; " +
                "it must start with a letter or digit and be at most 64 characters long."));

        // description / body: required non-empty for Create, optional-but-non-empty
        // for Update. Type and emptiness are checked here so both actions share one rule.
        string? description = null, body = null;
        if (json.TryGetProperty("description", out var descEl))
        {
            if (descEl.ValueKind != JsonValueKind.String)
                return Fail(new Error("InvalidParameterType",
                    $"'description' must be a string, but got {descEl.ValueKind}."));
            description = descEl.GetString()!;
        }
        if (json.TryGetProperty("body", out var bodyEl))
        {
            if (bodyEl.ValueKind != JsonValueKind.String)
                return Fail(new Error("InvalidParameterType",
                    $"'body' must be a string, but got {bodyEl.ValueKind}."));
            body = bodyEl.GetString()!;
        }

        string? provenanceSession = null;
        if (json.TryGetProperty("provenanceSession", out var provEl))
        {
            if (provEl.ValueKind != JsonValueKind.String)
                return Fail(new Error("InvalidParameterType",
                    $"'provenanceSession' must be a string, but got {provEl.ValueKind}."));
            provenanceSession = provEl.GetString()!;
        }

        switch (action)
        {
            case SkillManageAction.Create:
                if (description is null)
                    return Fail(new Error("MissingParameter",
                        "Missing required parameter 'description'. Create requires description and body."));
                if (description.Length == 0)
                    return Fail(new Error("InvalidParameterValue",
                        "'description' must be a non-empty string."));
                if (body is null)
                    return Fail(new Error("MissingParameter",
                        "Missing required parameter 'body'. Create requires description and body."));
                if (body.Length == 0)
                    return Fail(new Error("InvalidParameterValue",
                        "'body' must be a non-empty string."));
                break;

            case SkillManageAction.Update:
                if (description is not null && description.Length == 0)
                    return Fail(new Error("InvalidParameterValue",
                        "'description' must be a non-empty string."));
                if (body is not null && body.Length == 0)
                    return Fail(new Error("InvalidParameterValue",
                        "'body' must be a non-empty string."));
                if (description is null && body is null)
                    return Fail(new Error("InvalidParameterValue",
                        "Update changes nothing: provide at least one of 'description' or 'body'."));
                break;

            case SkillManageAction.Delete:
                // Deletion is permanent; anything other than an exact boolean true
                // (including a missing confirm) stops at the gate.
                if (!json.TryGetProperty("confirm", out var confirmEl) ||
                    confirmEl.ValueKind != JsonValueKind.True)
                    return Fail(new Error("InvalidParameterValue",
                        "'confirm' is required to delete a skill and must be exactly the boolean " +
                        "true. Deletion permanently removes current and history rows; re-issue " +
                        "with \"confirm\": true to proceed."));
                break;
        }

        return Result<SkillManageInput>.Success(
            new(action.Value, name, description, body, provenanceSession));
    }

    private static Result<SkillManageInput> MissingAction() =>
        Result<SkillManageInput>.Failure(new Error("MissingParameter",
            "Missing required parameter 'action'. This tool requires action " +
            "(Create, Update, or Delete) and name."));

    private static Result<SkillManageInput> MissingName() =>
        Result<SkillManageInput>.Failure(new Error("MissingParameter",
            "Missing required parameter 'name'. This tool requires action " +
            "(Create, Update, or Delete) and name."));

    private static Result<SkillManageInput> Fail(Error err) =>
        Result<SkillManageInput>.Failure(err);
}
