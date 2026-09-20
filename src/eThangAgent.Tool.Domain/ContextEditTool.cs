using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Fine-grained conversation-context edits: List renders the message list
///     with stable 1-based indexes, Remove deletes selected messages (whole
///     tool-call/result pairs together), Shorten replaces one message's content in
///     place. Mutations return the verbatim sentinel
///     '[context: shrank N message(s) ...]' — the loop's persistence contract for
///     replacing the transcript. Errors begin with 'Error [Code]:' and leave the
///     conversation untouched.</summary>
public sealed class ContextEditTool(IConversationContextService service) : ITool
{
  private const string ActionName = "action";
  private const string SelectionName = "selection";
  private const string TextName = "text";

  private readonly IConversationContextService _service = service ?? throw new ArgumentNullException(nameof(service));

  public ToolDefinition Definition { get; } = new(
      "context_edit",
      "Edit this conversation's own context. timeoutSeconds and action are mandatory: " +
      "action is exactly List, Remove, or Shorten (case-sensitive). List takes no other " +
      "parameters and prints one '[index] [Role] content' line per message - indexes are " +
      "1-based and always current. Remove deletes the selected messages: 'selection' is " +
      "exactly 'last N' or 'S..E' (1-based inclusive; a bare 'S' means S..S). Tool calls " +
      "and their results are removed together - removing one side fails with " +
      "UnansweredToolCall / DanglingToolResult, and removing the first message fails with " +
      "OrphanHead. Shorten replaces ONE message's content in place (same selection " +
      "syntax; a multi-message selection fails with AmbiguousSelection): 'text' is the " +
      "replacement - role, tool-call pairing, and timestamps stay intact. Use Remove for " +
      "redundant exchanges and Shorten to condense a verbose message. A successful " +
      "mutation returns '[context: shrank N message(s) ...]'; a selection naming nothing " +
      "fails with NothingSelected; an unparseable selection fails with InvalidSelection; " +
      "an out-of-bounds range fails with PositionOutOfRange. Errors begin with " +
      "`Error [Code]:`. The conversation is never emptied and never left protocol-invalid.",
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
          new ToolParameter(ActionName, ToolParameterType.Text,
              "Exactly List, Remove, or Shorten (case-sensitive)."),
          new ToolParameter(SelectionName, ToolParameterType.Text,
              "Remove and Shorten only: 'last N', 'S..E', or a bare position (1-based, current list)."),
          new ToolParameter(TextName, ToolParameterType.Text,
              "Shorten only: the replacement content for the selected message. Non-empty."),
      ],
      ["timeoutSeconds", "action"]);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<ContextEditInput> parsed = ContextEditInput.Create(input.JsonArguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(Err(parsed.Error));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return !budget.IsSuccess
      ? Task.FromResult(Err(budget.Error))
      : ToolExecution.RunAsync(input.Name, budget.Value.Timeout, _ => Task.FromResult(Run(parsed.Value)), ct);
  }

  private ToolResult Run(ContextEditInput input)
    => input.Action switch
    {
      ContextEditAction.List => new ToolResult(_service.List(), false),
      ContextEditAction.Remove => ToResult(_service.Remove(input.Selection!)),
      ContextEditAction.Shorten => ToResult(_service.Shorten(input.Selection!, input.Text!)),
      // Unnamed enum values cannot occur.
      _ => throw new InvalidOperationException("Unknown context_edit action."),
    };

  /// <summary>Renders a service result: success verbatim, failure as the canonical
  ///     'Error [Code]: message' line.</summary>
  private static ToolResult ToResult(Result<string> result)
      => result.IsSuccess ? new ToolResult(result.Value, false) : Err(result.Error);

  private static ToolResult Err(DomainError error)
      => new($"Error [{error.Code}]: {error.Message}", true);
}
