using System;
using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Shared JSON-argument parsing used by every tool. <see cref="ParseObject"/>
///     performs the strict object check every argument object must pass and is what the
///     per-tool input parsers build on (the optional <c>timeoutSeconds</c> key belongs
///     to their allowed sets). The mandatory-budget requirement itself is enforced once,
///     at the dispatch boundary, via <see cref="ToolCallEnvelopeParser"/>/
///     <see cref="ToolExecution"/> — never silently defaulted.</summary>
public static class ToolArguments
{
    /// <summary>Parses the raw arguments into a cloned JSON element, rejecting malformed
    ///     JSON and non-object input with typed errors.</summary>
    public static Result<JsonElement> ParseObject(string jsonArguments)
    {
        JsonElement json;
        try
        {
            using var doc = JsonDocument.Parse(jsonArguments);
            json = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return Result<JsonElement>.Failure(new Error("InvalidJsonArguments",
                $"Arguments are not valid JSON: {ex.Message}"));
        }
        if (json.ValueKind != JsonValueKind.Object)
            return Result<JsonElement>.Failure(new Error("InvalidJsonArguments",
                "Arguments must be a JSON object."));

        return Result<JsonElement>.Success(json);
    }

    /// <summary>Strict object check plus the mandatory <c>timeoutSeconds</c> budget.
    ///     Used by callers that own the whole contract in one place (zero-parameter
    ///     tools' argument checks).</summary>
    public static Result<(JsonElement Json, TimeSpan Timeout)> Parse(string jsonArguments)
    {
        var baseParse = ParseObject(jsonArguments);
        if (!baseParse.IsSuccess)
            return Result<(JsonElement, TimeSpan)>.Failure(baseParse.Error!);

        var timeout = ToolTimeout.Parse(baseParse.Value);
        if (!timeout.IsSuccess)
            return Result<(JsonElement, TimeSpan)>.Failure(timeout.Error!);

        return Result<(JsonElement, TimeSpan)>.Success((baseParse.Value, timeout.Value));
    }

    /// <summary>The validated execution budget for arguments that passed <see cref="Parse"/>.</summary>
    public static TimeSpan TimeoutOf(JsonElement json) =>
        ToolTimeout.Parse(json).Value;
}
