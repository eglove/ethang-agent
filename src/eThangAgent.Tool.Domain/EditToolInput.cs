using System.Globalization;
using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>A parsed edit request. Exactly one mode's parameter set is accepted:
///     anchor mode (literal text replacement selected by 'old' + 'all'/'occurrences'),
///     range mode (lines 'startLine'..'endLine' replaced by 'replacement'), or
///     hunks mode (an array of independent anchor hunks applied atomically).
///     Mixing modes is rejected at the door.</summary>
public abstract record EditToolInput(string Path)
{
  private const string PathName = "path";
  private const string OldName = "old";
  private const string NewName = "replacement";
  private const string AllName = "all";
  private const string OccurrencesName = "occurrences";
  private const string StartLineName = "startLine";
  private const string EndLineName = "endLine";
  private const string HunksName = "hunks";
  private const string AnchorRequirementText =
      "This tool requires path, old, and replacement, plus exactly one of 'all' or 'occurrences'.";
  private const string RangeRequirementText =
      "Range mode requires path, startLine, endLine, and replacement.";
  private const string HunkRequirementText =
      "Each hunk requires old and replacement, plus exactly one of 'all' or 'occurrences'.";

  private static readonly string[] AllowedNames =
      [PathName, OldName, NewName, AllName, OccurrencesName, StartLineName, EndLineName, HunksName,
          ToolTimeout.ParameterName];

  private static readonly string[] AllowedHunkNames =
      [OldName, NewName, AllName, OccurrencesName];

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

    bool hasRange = HasRange(json);
    bool hasAnchor = HasAnchor(json);
    bool hasHunks = json.TryGetProperty(HunksName, out JsonElement hunksEl);

    int modes = (hasRange ? 1 : 0) + (hasAnchor ? 1 : 0) + (hasHunks ? 1 : 0);
    if (modes > 1)
    {
      return Fail(ModeConflict());
    }

    Result<string> path = RequireNonEmptyText(
        ToolArguments.RequireString(json, PathName, AnchorRequirementText),
        "'path' must be a non-empty string.");
    if (!path.IsSuccess)
    {
      return Fail(path.Error);
    }

    // Hunks mode carries its own replacement per hunk, so the top-level parameter is
    // neither required nor accepted there — a stray one would be a second, ambiguous
    // statement of what to write.
    return hasHunks
      ? ParseHunksMode(json, hunksEl, path.Value)
      : ParseSingleEdit(json, path.Value, hasRange);
  }

  /// <summary>Anchor or range mode: both take one top-level 'replacement' (which may be
  ///     empty — deletion is explicit intent).</summary>
  private static Result<EditToolInput> ParseSingleEdit(JsonElement json, string path, bool hasRange)
  {
    Result<string> replacement = ToolArguments.RequireString(json, NewName, AnchorRequirementText);
    return replacement.IsSuccess
      ? ParseSingleEditWith(json, path, replacement.Value, hasRange)
      : Fail(replacement.Error);
  }

  private static Result<EditToolInput> ParseSingleEditWith(
      JsonElement json, string path, string replacement, bool hasRange) =>
      hasRange ? ParseRange(json, path, replacement) : ParseAnchor(json, path, replacement);

  /// <summary>Hunks mode: the replacement lives INSIDE each hunk, so a top-level one is
  ///     rejected rather than silently ignored.</summary>
  private static Result<EditToolInput> ParseHunksMode(JsonElement json, JsonElement hunksEl, string path) =>
      json.TryGetProperty(NewName, out _)
        ? Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
            "Hunks mode takes 'replacement' inside each hunk, not at the top level."))
        : ParseHunks(hunksEl, path);

  private static bool HasRange(JsonElement json) =>
      json.TryGetProperty(StartLineName, out _) || json.TryGetProperty(EndLineName, out _);

  private static bool HasAnchor(JsonElement json) =>
      json.TryGetProperty(OldName, out _)
      || json.TryGetProperty(AllName, out _)
      || json.TryGetProperty(OccurrencesName, out _);

  private static DomainError ModeConflict() =>
      new(ToolErrorCodes.InvalidParameterValue,
          "Provide exactly one mode: anchor (old + 'all'/'occurrences'), range " +
          "(startLine + endLine), or hunks (an array of {old, replacement, 'all'|'occurrences'}). " +
          "Anchor and range parameters cannot be combined with 'hunks' or with each other.");

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

    Result<(bool All, int Occurrences)> selector = ParseSelector(json, "");
    Result<EditToolInput> anchor = selector.IsSuccess
      ? Result.Success<EditToolInput>(
          new EditAnchorInput(path, replacement, old.Value, selector.Value.All, selector.Value.Occurrences))
      : Fail(selector.Error);
    return anchor;
  }

  /// <summary>Parses the hunks array. Every entry is validated exactly as an anchor-mode
  ///     call would be — same either-or selector, same non-empty anchor rule, same unknown
  ///     parameter rejection — and every error names the failing entry's index so one
  ///     correction lands on the right hunk.</summary>
  private static Result<EditToolInput> ParseHunks(JsonElement hunksEl, string path)
  {
    if (hunksEl.ValueKind != JsonValueKind.Array)
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'hunks' must be an array of objects, but got {hunksEl.ValueKind}."));
    }

    List<EditHunk> hunks = [];
    int index = 0;
    foreach (JsonElement entry in hunksEl.EnumerateArray())
    {
      Result<EditHunk> hunk = ParseHunk(entry, index);
      if (!hunk.IsSuccess)
      {
        return Fail(hunk.Error);
      }

      hunks.Add(hunk.Value);
      index++;
    }

    return hunks.Count == 0
      ? Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "'hunks' requires at least one hunk; use anchor or range mode for a single edit."))
      : Result.Success<EditToolInput>(new EditHunksInput(path, hunks));
  }

  private static Result<EditHunk> ParseHunk(JsonElement entry, int index)
  {
    string where = $"hunks[{index}]";
    if (entry.ValueKind != JsonValueKind.Object)
    {
      return HunkFail(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"{where} must be an object, but got {entry.ValueKind}."));
    }

    DomainError? unknown = RejectUnknownHunkParameters(entry, where);
    if (unknown is not null)
    {
      return HunkFail(unknown);
    }

    string requirement = $"{where}: {HunkRequirementText}";
    Result<string> old = RequireNonEmptyText(
        ToolArguments.RequireString(entry, OldName, requirement),
        $"{where}: 'old' must be a non-empty string — an empty anchor would match everywhere.");
    if (!old.IsSuccess)
    {
      return HunkFail(old.Error);
    }

    Result<string> replacement = ToolArguments.RequireString(entry, NewName, requirement);
    if (!replacement.IsSuccess)
    {
      return HunkFail(replacement.Error);
    }

    Result<(bool All, int Occurrences)> selector = ParseSelector(entry, where + ": ");
    return !selector.IsSuccess
      ? HunkFail(selector.Error)
      : Result.Success(new EditHunk(old.Value, replacement.Value, selector.Value.All, selector.Value.Occurrences));
  }

  private static DomainError? RejectUnknownHunkParameters(JsonElement entry, string where)
  {
    List<string> unknown = [.. entry.EnumerateObject()
        .Where(p => !AllowedHunkNames.Contains(p.Name, StringComparer.Ordinal))
        .Select(p => p.Name)];
    return unknown.Count == 0
      ? null
      : new DomainError(ToolErrorCodes.UnknownParameter,
          $"{where} carries unknown parameter(s): {string.Join(", ", unknown)}. " +
          $"Allowed: {string.Join(", ", AllowedHunkNames)}.");
  }

  private static Result<EditHunk> HunkFail(DomainError error) =>
      Result.Failure<EditHunk>(error);

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

  /// <summary>Exactly one of 'all' (boolean true) or 'occurrences' (integer ≥ 1).
  ///     <paramref name="prefix"/> names the hunk when this runs over a hunks entry.</summary>
  private static Result<(bool All, int Occurrences)> ParseSelector(JsonElement json, string prefix)
  {
    bool hasAll = json.TryGetProperty(AllName, out JsonElement allEl);
    bool hasOccurrences = json.TryGetProperty(OccurrencesName, out JsonElement occurrencesEl);
    return hasAll == hasOccurrences
      ? Result.Failure<(bool, int)>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"{prefix}Provide exactly one of 'all' (boolean true) or 'occurrences' (integer ≥ 1)."))
      : ChooseSelector(hasAll, allEl, occurrencesEl, prefix);
  }

  private static Result<(bool All, int Occurrences)> ChooseSelector(
      bool hasAll, JsonElement allEl, JsonElement occurrencesEl, string prefix) =>
      hasAll ? RequireExplicitAll(allEl, prefix) : RequireOccurrences(occurrencesEl, prefix);

  private static Result<(bool All, int Occurrences)> RequireExplicitAll(JsonElement allEl, string prefix)
  {
    Result<(bool All, int Occurrences)> all = allEl.ValueKind is JsonValueKind.True
      ? Result.Success((true, 0))
      : Result.Failure<(bool, int)>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"{prefix}'all' must be exactly true. Provide exactly one of " +
          "'all' (boolean true) or 'occurrences' (integer ≥ 1)."));
    return all;
  }

  private static Result<(bool All, int Occurrences)> RequireOccurrences(JsonElement occurrencesEl, string prefix)
  {
    if (occurrencesEl.ValueKind != JsonValueKind.Number || !occurrencesEl.TryGetInt32(out int occurrences))
    {
      return Result.Failure<(bool, int)>(new DomainError(ToolErrorCodes.InvalidParameterType,
          string.Create(CultureInfo.InvariantCulture,
              $"{prefix}'{OccurrencesName}' must be an integer, but got {occurrencesEl.ValueKind}.")));
    }

    Result<(bool All, int Occurrences)> positive = occurrences >= 1
      ? Result.Success((false, occurrences))
      : Result.Failure<(bool, int)>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          string.Create(CultureInfo.InvariantCulture,
              $"{prefix}'{OccurrencesName}' must be ≥ 1 (got {occurrences}).")));
    return positive;
  }

  private static Result<EditToolInput> Fail(DomainError err) =>
      Result.Failure<EditToolInput>(err);
}

/// <summary>Anchor mode: replace every occurrence (All) or exactly Occurrences of them.</summary>
public sealed record EditAnchorInput(string Path, string Replacement, string Old, bool All, int Occurrences)
    : EditToolInput(Path);

/// <summary>Range mode: replace lines StartLine..EndLine (1-based, inclusive).</summary>
public sealed record EditRangeInput(string Path, string Replacement, int StartLine, int EndLine)
    : EditToolInput(Path);

/// <summary>Hunks mode: several independent anchor hunks applied atomically, all or nothing.</summary>
public sealed record EditHunksInput(string Path, IReadOnlyList<EditHunk> Hunks)
    : EditToolInput(Path);
