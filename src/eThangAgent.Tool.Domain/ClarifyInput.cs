using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record ClarifyInput(string Question, IReadOnlyList<string>? Options, bool AllowFreeText)
{
    public static Result<ClarifyInput> Create(string jsonArguments)
    {
        var baseParse = ToolArguments.ParseObject(jsonArguments);
        if (!baseParse.IsSuccess)
            return Fail(baseParse.Error!);
        var json = baseParse.Value;

        var known = new HashSet<string>(["question", "options", "allowFreeText", ToolTimeout.ParameterName], StringComparer.Ordinal);
        var unknown = json.EnumerateObject()
            .Where(p => !known.Contains(p.Name))
            .Select(p => p.Name)
            .ToList();
        if (unknown.Count > 0)
            return Fail(new Error("UnknownParameter",
                $"Unknown parameter(s): {string.Join(", ", unknown)}. Allowed: question, options, allowFreeText, {ToolTimeout.ParameterName}."));

        if (!json.TryGetProperty("question", out var questionEl)) return Missing("question");
        if (questionEl.ValueKind != JsonValueKind.String) return WrongType("question", "string", questionEl.ValueKind);
        var question = questionEl.GetString()!;
        if (question.Length == 0)
            return Fail(new Error("InvalidParameterValue", "'question' must be a non-empty string."));

        IReadOnlyList<string>? options = null;
        if (json.TryGetProperty("options", out var optionsEl))
        {
            if (optionsEl.ValueKind != JsonValueKind.Array)
                return Fail(new Error("InvalidParameterType",
                    $"'options' must be an array of strings, but got {optionsEl.ValueKind}."));
            var items = new List<string>();
            foreach (var item in optionsEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    return Fail(new Error("InvalidParameterType",
                        $"'options' must contain only strings, but got {item.ValueKind}."));
                var option = item.GetString()!;
                if (option.Length == 0)
                    return Fail(new Error("InvalidParameterValue",
                        "'options' entries must be non-empty strings."));
                items.Add(option);
            }
            if (items.Count < 2)
                return Fail(new Error("InvalidParameterValue",
                    $"'options' must contain at least 2 entries when provided, but got {items.Count}."));
            options = items;
        }

        if (!json.TryGetProperty("allowFreeText", out var freeEl)) return Missing("allowFreeText");
        if (freeEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return WrongType("allowFreeText", "boolean", freeEl.ValueKind);
        var allowFreeText = freeEl.GetBoolean();

        // An options-free, free-text-blocked question can never succeed: every
        // answer would be rejected as FreeTextNotAllowed. Reject it at the boundary.
        if (!allowFreeText && options is null)
            return Fail(new Error("InvalidParameterValue",
                "'allowFreeText' is false but 'options' was not provided: without options " +
                "every answer would be rejected as free text. Provide at least 2 options " +
                "or set 'allowFreeText' to true."));

        return Result<ClarifyInput>.Success(new(question, options, allowFreeText));
    }

    private static Result<ClarifyInput> Missing(string n) =>
        Result<ClarifyInput>.Failure(new Error("MissingParameter",
            $"Missing required parameter '{n}'. This tool requires question and allowFreeText; options is optional."));

    private static Result<ClarifyInput> WrongType(string n, string e, JsonValueKind a) =>
        Result<ClarifyInput>.Failure(new Error("InvalidParameterType",
            $"'{n}' must be a {e}, but got {a}."));

    private static Result<ClarifyInput> Fail(Error err) =>
        Result<ClarifyInput>.Failure(err);
}
