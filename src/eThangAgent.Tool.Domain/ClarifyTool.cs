using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed class ClarifyTool : ITool
{
    private readonly IClarifyChannel _channel;

    public ToolDefinition Definition { get; } = new(
        "clarify",
        "Ask the human a single clarifying question and receive their answer. The question " +
        "is presented with numbered options when options are provided; the human replies with " +
        "an option number (1-based) or free text. Set allowFreeText to false to restrict the " +
        "human to the numbered options. Output is a single annotation line in [brackets]: " +
        "`[clarify] answered: <text>` where <text> is the chosen option text or the free-text " +
        "answer verbatim. If the human cancels (Ctrl+C or end of input) the error is " +
        "`Error [Cancelled]:`. Other errors begin with `Error [Code]:`.",
        [
            new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.Integer, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("question", ToolParameterType.String,
                "The question to ask. Required, non-empty."),
            new ToolParameter("options", ToolParameterType.StringArray,
                "A JSON array of two or more answer-option strings, presented as a numbered " +
                "list. Optional; omit for a free-text-only question. Example: " +
                "[\"first option\", \"second option\"]."),
            new ToolParameter("allowFreeText", ToolParameterType.Boolean,
                "true to let the human answer in their own words, false to require an option number."),
        ],
        ["timeoutSeconds", "question", "allowFreeText"]);

    public ClarifyTool(IClarifyChannel channel)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
    }

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
        var parsed = ClarifyInput.Create(input.JsonArguments);
        if (!parsed.IsSuccess)
            return Task.FromResult(Err(parsed.Error!));

        var budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
        if (!budget.IsSuccess)
            return Task.FromResult(Err(budget.Error!));

        var v = parsed.Value!;
        return ToolExecution.RunAsync(input.Name, budget.Value!.Timeout, token =>
            AskAsync(v, token), ct);
    }

    private async Task<ToolResult> AskAsync(ClarifyInput v, CancellationToken ct)
    {
        var asked = await _channel.AskAsync(
            new ClarifyQuestion(v.Question, v.Options ?? [], v.AllowFreeText), ct);
        if (!asked.IsSuccess)
            return Err(asked.Error!);

        var raw = asked.Value!;
        string answered;
        if (v.Options is { Count: > 0 } && int.TryParse(raw.Trim(), out var selection))
        {
            if (selection < 1 || selection > v.Options.Count)
                return Err(new Error("InvalidSelection",
                    $"'{raw.Trim()}' is not a valid selection; choose a number between 1 and {v.Options.Count}."));
            answered = v.Options[selection - 1];
        }
        else if (!v.AllowFreeText)
        {
            return Err(new Error("FreeTextNotAllowed",
                "Free text is not allowed for this question; answer with one of the presented option numbers."));
        }
        else
        {
            answered = raw;
        }

        return new ToolResult($"[clarify] answered: {answered}", false);
    }

    private static ToolResult Err(Error error) => new($"Error [{error.Code}]: {error.Message}", true);
}
