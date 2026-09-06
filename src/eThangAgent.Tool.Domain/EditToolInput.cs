using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>A parsed edit request. Exactly one mode's parameter set is accepted:
///     anchor mode (literal text replacement selected by 'old' + 'all'/'occurrences')
///     or range mode (lines 'startLine'..'endLine' replaced by 'replacement').
///     Mixing modes is rejected at the door.</summary>
public abstract record EditToolInput(string Path, string Replacement)
{
  private const string PathName = "path";
  private const string OldName = "old";
  private const string NewName = "replacement";
  private const string AllName = "all";
  private const string OccurrencesName = "occurrences";
  private const string StartLineName = "startLine";
  private const string EndLineName = "endLine";
  private const string AnchorRequirementText =
      "This tool requires path, old, and replacement, plus exactly one of 'all' or 'occurrences'.";
  private const string RangeRequirementText =
      "Range mode requires path, startLine, endLine, and replacement.";

  private static readonly string[] AllowedNames =
      [PathName, OldName, NewName, AllName, OccurrencesName, StartLineName, EndLineName, ToolTimeout.ParameterName];

  public static Result<EditToolInput> Create(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Fail(baseParse.Error);
    }

    JsonElement json = baseParse.Value;

    // The JSON parameter was renamed from 'new' (a C# keyword that breaks scripted calls)
    // to 'replacement'. Name the fix instead of a generic unknown-parameter error.
    if (json.TryGetProperty("new", out _))
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "'new' is not accepted; the parameter is named 'replacement'."));
    }

    DomainError? unknown = ToolArguments.RejectUnknownParameters(json, AllowedNames);
    if (unknown is not null)
    {
      return Fail(unknown);
    }

    bool hasRange = json.TryGetProperty(StartLineName, out _) || json.TryGetProperty(EndLineName, out _);
    bool hasAnchor = json.TryGetProperty(OldName, out _)
        || json.TryGetProperty(AllName, out _)
        || json.TryGetProperty(OccurrencesName, out _);
    if (hasRange && hasAnchor)
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "Anchor parameters ('old', 'all', 'occurrences') cannot be combined with range parameters " +
          "('startLine', 'endLine'). Provide exactly one mode: anchor (old + 'all'/'occurrences') " +
          "or range (startLine + endLine)."));
    }

    Result<string> path = RequireNonEmptyText(
        ToolArguments.RequireString(json, PathName, AnchorRequirementText),
        "'path' must be a non-empty string.");
    if (!path.IsSuccess)
    {
      return Fail(path.Error);
    }

    // 'replacement' may be empty: deletion is explicit intent in both modes.
    Result<string> replacement = ToolArguments.RequireString(json, NewName, AnchorRequirementText);
    if (!replacement.IsSuccess)
    {
      return Fail(replacement.Error);
    }

    Result<EditToolInput> parsed = hasRange
      ? ParseRange(json, path.Value, replacement.Value)
      : ParseAnchor(json, path.Value, replacement.Value);
    return parsed;
  }

  /// <summary>Rejects an empty string after the type check passes.</summary>
  private static Result<string> RequireNonEmptyText(Result<string> text, string emptyMessage)
  {
    if (!text.IsSuccess)
    {
      return text;
    }

    Result<string> nonEmpty = text.Value.Length > 0
      ? text
      : Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterValue, emptyMessage));
    return nonEmpty;
  }

  private static Result<EditToolInput> ParseAnchor(JsonElement json, string path, string replacement)
  {
    Result<string> old = RequireNonEmptyText(
        ToolArguments.RequireString(json, OldName, AnchorRequirementText),
        "'old' must be a non-empty string — an empty anchor would match everywhere.");
    if (!old.IsSuccess)
    {
      return Fail(old.Error);
    }

    Result<(bool All, int Occurrences)> selector = ParseSelector(json);
    Result<EditToolInput> anchor = selector.IsSuccess
      ? Result.Success<EditToolInput>(
          new EditAnchorInput(path, replacement, old.Value, selector.Value.All, selector.Value.Occurrences))
      : Fail(selector.Error);
    return anchor;
  }

  private static Result<EditToolInput> ParseRange(JsonElement json, string path, string replacement)
  {
    Result<int> startLine = RequireLine(json, StartLineName);
    if (!startLine.IsSuccess)
    {
      return Fail(startLine.Error);
    }

    Result<int> endLine = RequireLine(json, EndLineName);
    if (!endLine.IsSuccess)
    {
      return Fail(endLine.Error);
    }

    Result<EditToolInput> ordered = startLine.Value <= endLine.Value
      ? Result.Success<EditToolInput>(new EditRangeInput(path, replacement, startLine.Value, endLine.Value))
      : Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'startLine' ({startLine.Value}) must not exceed 'endLine' ({endLine.Value})."));
    return ordered;
  }

  /// <summary>One 1-based line number: present, an integer, and at least 1.</summary>
  private static Result<int> RequireLine(JsonElement json, string name)
  {
    if (!json.TryGetProperty(name, out JsonElement lineEl))
    {
      return Result.Failure<int>(new DomainError(ToolErrorCodes.MissingParameter,
          $"Missing required parameter '{name}'. {RangeRequirementText}"));
    }

    if (lineEl.ValueKind != JsonValueKind.Number || !lineEl.TryGetInt32(out int line))
    {
      return Result.Failure<int>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'{name}' must be an integer, but got {lineEl.ValueKind}."));
    }

    Result<int> inRange = line >= 1
      ? Result.Success(line)
      : Result.Failure<int>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'{name}' must be ≥ 1 (got {line})."));
    return inRange;
  }

  /// <summary>Exactly one of 'all' (boolean true) or 'occurrences' (integer ≥ 1).</summary>
  private static Result<(bool All, int Occurrences)> ParseSelector(JsonElement json)
  {
    bool hasAll = json.TryGetProperty(AllName, out JsonElement allEl);
    bool hasOccurrences = json.TryGetProperty(OccurrencesName, out JsonElement occurrencesEl);
    if (hasAll == hasOccurrences)
    {
      return Result.Failure<(bool, int)>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "Provide exactly one of 'all' (boolean true) or 'occurrences' (integer ≥ 1)."));
    }

    Result<(bool All, int Occurrences)> selector = hasAll
      ? RequireExplicitAll(allEl)
      : RequireOccurrences(occurrencesEl);
    return selector;
  }

  private static Result<(bool All, int Occurrences)> RequireExplicitAll(JsonElement allEl)
  {
    Result<(bool All, int Occurrences)> all = allEl.ValueKind is JsonValueKind.True
      ? Result.Success((true, 0))
      : Result.Failure<(bool, int)>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "'all' must be exactly true. Provide exactly one of " +
          "'all' (boolean true) or 'occurrences' (integer ≥ 1)."));
    return all;
  }

  private static Result<(bool All, int Occurrences)> RequireOccurrences(JsonElement occurrencesEl)
  {
    if (occurrencesEl.ValueKind != JsonValueKind.Number || !occurrencesEl.TryGetInt32(out int occurrences))
    {
      return Result.Failure<(bool, int)>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'{OccurrencesName}' must be an integer, but got {occurrencesEl.ValueKind}."));
    }

    Result<(bool All, int Occurrences)> positive = occurrences >= 1
      ? Result.Success((false, occurrences))
      : Result.Failure<(bool, int)>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'{OccurrencesName}' must be ≥ 1 (got {occurrences})."));
    return positive;
  }

  private static Result<EditToolInput> Fail(DomainError err) =>
      Result.Failure<EditToolInput>(err);
}

/// <summary>Anchor mode: replace every occurrence (All) or exactly Occurrences of them.</summary>
public sealed record EditAnchorInput(string Path, string Replacement, string Old, bool All, int Occurrences)
    : EditToolInput(Path, Replacement);

/// <summary>Range mode: replace lines StartLine..EndLine (1-based, inclusive).</summary>
public sealed record EditRangeInput(string Path, string Replacement, int StartLine, int EndLine)
    : EditToolInput(Path, Replacement);
