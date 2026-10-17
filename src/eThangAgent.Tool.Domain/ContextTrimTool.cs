using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Lightweight cleanup of THIS conversation's tool results: Trim replaces
///     the selected tool results with one-line relocation markers (what produced
///     them and how to re-fetch it), Shorten replaces ONE selected tool result
///     with the caller's excerpt. Only tool results are reachable - user,
///     assistant, and system messages fail with ToolOnlySelection; context_edit
///     owns those. Mutations return the verbatim sentinel
///     '[context: shrank N message(s) ...]' - the loop's persistence contract for
///     replacing the transcript. Errors begin with 'Error [Code]:' and leave the
///     conversation untouched.</summary>
public sealed class ContextTrimTool(IConversationContextService service) : ITool
{
  private const string ActionName = "action";
  private const string SelectionName = "selection";
  private const string TextName = "text";

  private readonly IConversationContextService _service = service ?? throw new ArgumentNullException(nameof(service));

  public ToolDefinition Definition { get; } = new(
      "context_trim",
      "Lightweight cleanup of THIS conversation's tool results. action is exactly Trim " +
      "or Shorten (case-sensitive); selection is 'last N', 'S..E', or a bare position " +
      "(1-based, current list - run context_edit List to see it). Trim replaces the " +
      "selected tool results with one-line markers preserving what produced them and " +
      "how to re-fetch it; Shorten replaces ONE selected tool result with your excerpt " +
      "text. ONLY tool results can be trimmed - user, assistant, and system messages " +
      "fail with ToolOnlySelection; use context_edit for those. WHEN: after a large " +
      "tool result (a long file read, a web fetch, big command output) has served its " +
      "purpose, trim it - keep concrete identifiers in a Shorten excerpt before " +
      "trimming anything you still need. Success returns '[context: shrank N " +
      "message(s) ...]'. Errors begin with 'Error [Code]:'.",
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
          new ToolParameter(ActionName, ToolParameterType.Text,
              "Exactly Trim or Shorten (case-sensitive)."),
          new ToolParameter(SelectionName, ToolParameterType.Text,
              "'last N', 'S..E', or a bare position (1-based, current list)."),
          new ToolParameter(TextName, ToolParameterType.Text,
              "Shorten only: the excerpt to keep for the selected tool result. Non-empty."),
      ],
      ["timeoutSeconds", "action"]);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<ContextTrimInput> parsed = ContextTrimInput.Create(input.JsonArguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(Err(parsed.Error));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return !budget.IsSuccess
      ? Task.FromResult(Err(budget.Error))
      : ToolExecution.RunAsync(input.Name, budget.Value.Timeout, _ => Task.FromResult(Run(parsed.Value)), ct);
  }

  private ToolResult Run(ContextTrimInput input)
    => input.Action switch
    {
      ContextTrimAction.Trim => ToResult(_service.TrimToolResults(input.Selection)),
      ContextTrimAction.Shorten => ToResult(_service.ShortenToolResult(input.Selection, input.Text!)),
      // Unnamed enum values cannot occur.
      _ => throw new InvalidOperationException("Unknown context_trim action."),
    };

  /// <summary>Renders a service result: success verbatim, failure as the canonical
  ///     'Error [Code]: message' line.</summary>
  private static ToolResult ToResult(Result<string> result)
      => result.IsSuccess ? new ToolResult(result.Value, false) : Err(result.Error);

  private static ToolResult Err(DomainError error)
      => new($"Error [{error.Code}]: {error.Message}", true);
}
