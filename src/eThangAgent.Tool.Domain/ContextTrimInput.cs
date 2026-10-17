using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public enum ContextTrimAction { Trim, Shorten }

/// <summary>Strict input for the context_trim tool: timeoutSeconds and action are
///     mandatory; selection is always required, text exactly when the action is
///     Shorten. Unknown parameters and wrong types are typed errors - nothing is
///     coerced, defaulted, or clamped.</summary>
public sealed record ContextTrimInput(ContextTrimAction Action, string Selection, string? Text)
{
  private const string ActionName = "action";
  private const string SelectionName = "selection";
  private const string TextName = "text";

  private static readonly string[] AllowedNames = [ActionName, SelectionName, TextName, ToolTimeout.ParameterName];

  private static readonly string[] AllowedActions = ["Trim", "Shorten"];

  public static Result<ContextTrimInput> Create(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Result.Failure<ContextTrimInput>(baseParse.Error);
    }

    JsonElement json = baseParse.Value;
    DomainError? unknown = ToolArguments.RejectUnknownParameters(json, AllowedNames);
    if (unknown is not null)
    {
      return Result.Failure<ContextTrimInput>(unknown);
    }

    Result<string> action = ToolArguments.RequireString(json, ActionName,
        "This tool requires action (Trim or Shorten).");
    if (!action.IsSuccess)
    {
      return Result.Failure<ContextTrimInput>(action.Error);
    }

    Result<ContextTrimAction> parsed = ToolArguments.ParseEnum<ContextTrimAction>(ActionName, action.Value, AllowedActions);
    if (!parsed.IsSuccess)
    {
      return Result.Failure<ContextTrimInput>(parsed.Error);
    }

    Result<string?> selection = ToolArguments.OptionalString(json, SelectionName);
    if (!selection.IsSuccess)
    {
      return Result.Failure<ContextTrimInput>(selection.Error);
    }

    Result<string?> text = ToolArguments.OptionalString(json, TextName);
    if (!text.IsSuccess)
    {
      return Result.Failure<ContextTrimInput>(text.Error);
    }

    DomainError? violation = ValidateForAction(parsed.Value, selection.Value, text.Value);
    return violation is null
      ? Result.Success(new ContextTrimInput(parsed.Value, selection.Value, text.Value))
      : Result.Failure<ContextTrimInput>(violation);
  }

  /// <summary>Action-level rules: the parameters each action demands, no more.
  ///     Trim rejects text rather than silently ignoring it.</summary>
  private static DomainError? ValidateForAction(ContextTrimAction action, string? selection, string? text)
  {
    if (string.IsNullOrEmpty(selection))
    {
      return new DomainError(ToolErrorCodes.MissingParameter,
          "Missing required parameter 'selection'. Trim and Shorten require a selection ('last N', 'S..E', or a bare position).");
    }

    if (action == ContextTrimAction.Trim)
    {
      return text is not null
        ? new DomainError(ToolErrorCodes.InvalidParameterValue,
            "'text' is not accepted for Trim: it replaces content with a marker, not an excerpt.")
        : null;
    }

    // Shorten.
    return string.IsNullOrEmpty(text)
      ? new DomainError(ToolErrorCodes.MissingParameter,
          "Missing required parameter 'text'. Shorten requires the excerpt to keep.")
      : null;
  }
}
