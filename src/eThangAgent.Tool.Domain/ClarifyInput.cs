using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record ClarifyInput(string Question, IReadOnlyList<string>? Options, bool AllowFreeText)
{
    public static Result<ClarifyInput> Create(string jsonArguments)
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

        var known = new HashSet<string>(["question", "options", "allowFreeText"], StringComparer.Ordinal);
        var unknown = json.EnumerateObject()
            .Where(p => !known.Contains(p.Name))
            .Select(p => p.Name)
            .ToList();
        if (unknown.Count > 0)
            return Fail(new Error("UnknownParameter",
                $"Unknown parameter(s): {string.Join(", ", unknown)}. Allowed: question, options, allowFreeText."));

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
