using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public enum ContextEditAction { List, Remove, Shorten, Trim }

/// <summary>Strict input for the context_edit tool: timeoutSeconds and action are
///     mandatory; selection and text are required exactly when their action demands
///     them. Unknown parameters and wrong types are typed errors - nothing is
///     coerced, defaulted, or clamped.</summary>
public sealed record ContextEditInput(ContextEditAction Action, string? Selection, string? Text)
{
  private const string ActionName = "action";
  private const string SelectionName = "selection";
  private const string TextName = "text";

  private static readonly string[] AllowedNames = [ActionName, SelectionName, TextName, ToolTimeout.ParameterName];

  private static readonly string[] AllowedActions = ["List", "Remove", "Shorten", "Trim"];

  public static Result<ContextEditInput> Create(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Result.Failure<ContextEditInput>(baseParse.Error);
    }

    JsonElement json = baseParse.Value;
    DomainError? unknown = ToolArguments.RejectUnknownParameters(json, AllowedNames);
    if (unknown is not null)
    {
      return Result.Failure<ContextEditInput>(unknown);
    }

    Result<string> action = ToolArguments.RequireString(json, ActionName,
        "This tool requires action (List, Remove, Shorten, or Trim).");
    if (!action.IsSuccess)
    {
      return Result.Failure<ContextEditInput>(action.Error);
    }

    // Action names are matched case-insensitively: 'list' and 'List' are the same
    // request, and refusing one only costs the caller a round trip.
    string? canonical = AllowedActions.FirstOrDefault(a =>
        string.Equals(a, action.Value, StringComparison.OrdinalIgnoreCase));
    Result<ContextEditAction> parsed = ToolArguments.ParseEnum<ContextEditAction>(ActionName, canonical ?? action.Value, AllowedActions);
    if (!parsed.IsSuccess)
    {
      return Result.Failure<ContextEditInput>(parsed.Error);
    }

    Result<string?> selection = ToolArguments.OptionalString(json, SelectionName);
    if (!selection.IsSuccess)
    {
      return Result.Failure<ContextEditInput>(selection.Error);
    }

    Result<string?> text = ToolArguments.OptionalString(json, TextName);
    if (!text.IsSuccess)
    {
      return Result.Failure<ContextEditInput>(text.Error);
    }

    DomainError? violation = ValidateForAction(parsed.Value, selection.Value, text.Value);
    return violation is null
      ? Result.Success(new ContextEditInput(parsed.Value, selection.Value, text.Value))
      : Result.Failure<ContextEditInput>(violation);
  }

  /// <summary>Action-level rules: the parameters each action demands, no more.
  ///     List with extras is rejected rather than silently ignored.</summary>
  private static DomainError? ValidateForAction(ContextEditAction action, string? selection, string? text)
  {
    if (action == ContextEditAction.List)
    {
      return selection is not null || text is not null
        ? new DomainError(ToolErrorCodes.InvalidParameterValue,
            "'selection' and 'text' are not accepted for List: it takes no other parameters.")
        : null;
    }

    if (action == ContextEditAction.Trim)
    {
      return text is not null
        ? new DomainError(ToolErrorCodes.InvalidParameterValue,
            "'text' is not accepted for Trim: it replaces content with a marker, not an excerpt.")
        : null;
    }

    if (action == ContextEditAction.Remove)
    {
      return string.IsNullOrEmpty(selection)
        ? new DomainError(ToolErrorCodes.MissingParameter,
            "Missing required parameter 'selection'. Remove requires a selection ('last N' or 'S..E').")
        : null;
    }

    // Shorten.
    if (string.IsNullOrEmpty(selection))
    {
      return new DomainError(ToolErrorCodes.MissingParameter,
          "Missing required parameter 'selection'. Shorten requires a selection naming exactly one message.");
    }

    DomainError? textViolation = string.IsNullOrEmpty(text)
      ? new DomainError(ToolErrorCodes.MissingParameter,
          "Missing required parameter 'text'. Shorten requires the replacement content.")
      : null;
    return textViolation;
  }
}
