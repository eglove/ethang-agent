using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>The experiment tool's seven actions, in the tool's documented spelling.</summary>
public enum ExperimentAction { RecordHypothesis, RecordPrediction, RecordVerdict, RecordObservation, RecordConclusion, RecordSkip, List, Summary }

/// <summary>A prediction's outcome: exactly the three values the finish gate understands.</summary>
public enum ExperimentVerdict { Confirmed, Refuted, Inconclusive }

/// <summary>Strictly parsed arguments for one experiment tool call. Every field is
///     action-dependent: the parser rejects a parameter the chosen action does not
///     accept rather than silently dropping it, and nothing is coerced or defaulted.</summary>
public sealed record ExperimentInput(
    ExperimentAction Action,
    string Slug,
    int? Id,
    string? Text,
    string? FalsifiedBy,
    string? Evidence,
    ExperimentVerdict? Verdict)
{
  private const string ActionName = "action";
  private const string SlugName = "slug";
  private const string IdName = "id";
  private const string TextName = "text";
  private const string FalsifiedByName = "falsifiedBy";
  private const string EvidenceName = "evidence";
  private const string VerdictName = "verdict";

  private static readonly string[] AllowedNames =
  [
      ActionName, SlugName, IdName, TextName, FalsifiedByName, EvidenceName, VerdictName,
      ToolTimeout.ParameterName,
  ];

  /// <summary>The action spellings, exact and case-sensitive, as the model writes them.</summary>
  private static readonly Dictionary<string, ExperimentAction> ActionWords = new(StringComparer.Ordinal)
  {
    ["record-hypothesis"] = ExperimentAction.RecordHypothesis,
    ["record-prediction"] = ExperimentAction.RecordPrediction,
    ["record-verdict"] = ExperimentAction.RecordVerdict,
    ["record-observation"] = ExperimentAction.RecordObservation,
    ["record-conclusion"] = ExperimentAction.RecordConclusion,
    ["record-skip"] = ExperimentAction.RecordSkip,
    ["list"] = ExperimentAction.List,
    ["summary"] = ExperimentAction.Summary,
  };

  /// <summary>The action vocabulary as the model reads it: <see cref="ActionWords"/>'s keys
  ///     joined in declaration order. THE one source of truth — the tool's parameter text
  ///     and both rejection messages read it, so a vocabulary change cannot leave one of
  ///     them describing a set the tool does not accept (issue #284: the missing-action
  ///     message predated <c>summary</c>, so a model that omitted <c>action</c> was told a
  ///     vocabulary that could not express the cheap finish-gate check).</summary>
  public static string ActionVocabulary => string.Join(", ", ActionWords.Keys);

  private static readonly Dictionary<string, ExperimentVerdict> VerdictWords = new(StringComparer.Ordinal)
  {
    ["confirmed"] = ExperimentVerdict.Confirmed,
    ["refuted"] = ExperimentVerdict.Refuted,
    ["inconclusive"] = ExperimentVerdict.Inconclusive,
  };

  /// <summary>Parses and validates the raw JSON arguments. Field types are exact; the
  ///     per-action rules run after every field parses, in the tool's documented order.
  ///     A validation failure means the caller must append nothing.</summary>
  public static Result<ExperimentInput> Create(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Fail(baseParse.Error);
    }

    DomainError? unknown = ToolArguments.RejectUnknownParameters(baseParse.Value, AllowedNames);
    return unknown is not null ? Fail(unknown) : ParseFields(baseParse.Value);
  }

  /// <summary>Field-level parsing in declaration order, so the first violated rule is the
  ///     one reported: action, slug, id, then the action-dependent tail.</summary>
  private static Result<ExperimentInput> ParseFields(JsonElement json)
  {
    Result<ExperimentAction> action = ParseAction(json);
    if (!action.IsSuccess)
    {
      return Fail(action.Error);
    }

    Result<string> slug = ToolArguments.RequireString(json, SlugName,
        "This tool requires slug: the plan slug whose ledger sci.<slug>/ledger is read or appended.");
    if (!slug.IsSuccess)
    {
      return Fail(slug.Error);
    }

    DomainError? badSlug = ValidateSlug(slug.Value);
    if (badSlug is not null)
    {
      return Fail(badSlug);
    }

    Result<int?> id = ParseId(json);
    return id.IsSuccess
      ? ParseRemaining(json, action.Value, slug.Value, id.Value)
      : Fail(id.Error);
  }

  private static Result<ExperimentInput> ParseRemaining(JsonElement json, ExperimentAction action,
      string slug, int? id)
  {
    Result<string?> text = ToolArguments.OptionalString(json, TextName);
    if (!text.IsSuccess)
    {
      return Fail(text.Error);
    }

    Result<string?> falsifiedBy = ToolArguments.OptionalString(json, FalsifiedByName);
    if (!falsifiedBy.IsSuccess)
    {
      return Fail(falsifiedBy.Error);
    }

    Result<string?> evidence = ToolArguments.OptionalString(json, EvidenceName);
    if (!evidence.IsSuccess)
    {
      return Fail(evidence.Error);
    }

    Result<ExperimentVerdict?> verdict = ParseVerdict(json);
    if (!verdict.IsSuccess)
    {
      return Fail(verdict.Error);
    }

    DomainError? invalid = ValidateForAction(action, id, text.Value,
        falsifiedBy.Value, evidence.Value, verdict.Value);
    return invalid is null
      ? Result.Success(new ExperimentInput(action, slug, id, text.Value,
          falsifiedBy.Value, evidence.Value, verdict.Value))
      : Fail(invalid);
  }

  /// <summary>The action's own parameter set, echoed in rejection messages so the model
  ///     can self-correct without re-reading the whole contract.</summary>
  private static string[] AcceptedFor(ExperimentAction action) => action switch
  {
    ExperimentAction.RecordHypothesis => [ActionName, SlugName, TextName, IdName],
    ExperimentAction.RecordPrediction => [ActionName, SlugName, IdName, TextName, FalsifiedByName],
    ExperimentAction.RecordVerdict => [ActionName, SlugName, IdName, VerdictName, EvidenceName],
    ExperimentAction.RecordObservation => [ActionName, SlugName, TextName],
    ExperimentAction.RecordConclusion => [ActionName, SlugName, TextName],
    ExperimentAction.RecordSkip => [ActionName, SlugName, TextName, IdName],
    ExperimentAction.List => [ActionName, SlugName],
    ExperimentAction.Summary => [ActionName, SlugName],
    // Unnamed enum values cannot occur.
    _ => throw new InvalidOperationException("Unknown experiment action."),
  };

  private static Result<ExperimentAction> ParseAction(JsonElement json)
  {
    Result<string> text = ToolArguments.RequireString(json, ActionName,
        "This tool requires action (" + ActionVocabulary + ").");
    return text.IsSuccess
      ? MatchAction(text.Value)
      : Result.Failure<ExperimentAction>(text.Error);
  }

  private static Result<ExperimentAction> MatchAction(string text) =>
      ActionWords.TryGetValue(text, out ExperimentAction action)
        ? Result.Success(action)
        : Result.Failure<ExperimentAction>(new DomainError(ToolErrorCodes.InvalidParameterValue,
            $"'{ActionName}' must be exactly one of {ActionVocabulary} " +
            $"(case-sensitive), but got '{text}'."));

  private static Result<int?> ParseId(JsonElement json)
  {
    Result<int?> id = ToolArguments.OptionalInt(json, IdName);
    return id.IsSuccess ? ValidateIdRange(id) : id;
  }

  private static Result<int?> ValidateIdRange(Result<int?> id) =>
      id.Value is { } value && value <= 0
        ? Result.Failure<int?>(new DomainError(ToolErrorCodes.InvalidParameterValue,
            $"'{IdName}' must be a positive integer, but got {value}."))
        : id;

  private static Result<ExperimentVerdict?> ParseVerdict(JsonElement json)
  {
    Result<string?> text = ToolArguments.OptionalString(json, VerdictName);
    return text.IsSuccess
      ? MatchVerdict(text.Value)
      : Result.Failure<ExperimentVerdict?>(text.Error);
  }

  private static Result<ExperimentVerdict?> MatchVerdict(string? outcome) =>
      outcome is not { } word
        ? Result.Success<ExperimentVerdict?>(null)
        : MatchVerdictWord(word);

  private static Result<ExperimentVerdict?> MatchVerdictWord(string word) =>
      VerdictWords.TryGetValue(word, out ExperimentVerdict verdict)
        ? Result.Success<ExperimentVerdict?>(verdict)
        : Result.Failure<ExperimentVerdict?>(new DomainError(ToolErrorCodes.InvalidParameterValue,
            $"'{VerdictName}' must be exactly one of {string.Join(", ", VerdictWords.Keys)} " +
            $"(case-sensitive), but got '{word}'."));

  /// <summary>The slug names the ledger key <c>sci.&lt;slug&gt;/ledger</c>, so it must be
  ///     a single key segment: non-empty, whitespace-free, and slash-free.</summary>
  private static DomainError? ValidateSlug(string slug) =>
      slug.Trim().Length == 0 ? EmptySlug() : SlugSegmentError(slug);

  private static DomainError EmptySlug() =>
      new(ToolErrorCodes.InvalidParameterValue, $"'{SlugName}' must be a non-empty string.");

  private static DomainError? SlugSegmentError(string slug) =>
      slug.Contains('/', StringComparison.Ordinal) || slug.Any(char.IsWhiteSpace)
        ? new DomainError(ToolErrorCodes.InvalidParameterValue,
            $"'{SlugName}' must be a single key segment: no '/' and no whitespace, because " +
            $"the ledger key is 'sci.<slug>/ledger'. Got '{slug}'.")
        : null;

  /// <summary>Action-level rules, checked after every field parses: first the parameters
  ///     this action does not accept (rejected, never dropped), then the required ones.</summary>
  private static DomainError? ValidateForAction(ExperimentAction action, int? id, string? text,
      string? falsifiedBy, string? evidence, ExperimentVerdict? verdict) =>
      RejectForAction(action, id, text, falsifiedBy, evidence, verdict) ?? RequiredForAction(
          action, id, text, falsifiedBy, evidence, verdict);

  private static DomainError? RequiredForAction(ExperimentAction action, int? id, string? text,
      string? falsifiedBy, string? evidence, ExperimentVerdict? verdict) => action switch
      {
        ExperimentAction.RecordHypothesis => RequireValue(TextName, text),
        ExperimentAction.RecordPrediction => FirstNonNull(
            RequireValue(TextName, text),
            RequireValue(FalsifiedByName, falsifiedBy),
            RequireId(action, id)),
        ExperimentAction.RecordVerdict => ValidateVerdict(id, evidence, verdict),
        ExperimentAction.RecordObservation => RequireValue(TextName, text),
        ExperimentAction.RecordConclusion => RequireValue(TextName, text),
        ExperimentAction.RecordSkip => RequireValue(TextName, text),
        ExperimentAction.List => null,
        ExperimentAction.Summary => null,
        // Unnamed enum values cannot occur.
        _ => throw new InvalidOperationException("Unknown experiment action."),
      };

  private static DomainError? RejectForAction(ExperimentAction action, int? id, string? text,
      string? falsifiedBy, string? evidence, ExperimentVerdict? verdict)
  {
    string[] accepted = AcceptedFor(action);
    (string Name, bool Supplied)[] supplied =
    [
        (IdName, id is not null),
        (TextName, text is not null),
        (FalsifiedByName, falsifiedBy is not null),
        (EvidenceName, evidence is not null),
        (VerdictName, verdict is not null),
    ];
    string? rejected = supplied
        .Where(p => p.Supplied && !accepted.Contains(p.Name, StringComparer.Ordinal))
        .Select(p => p.Name)
        .FirstOrDefault();
    return rejected is null
      ? null
      : new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'{rejected}' is not accepted for action '{ActionText(action)}'. " +
          $"This action accepts: {string.Join(", ", accepted)}.");
  }

  private static DomainError? ValidateVerdict(int? id, string? evidence, ExperimentVerdict? verdict) =>
      RequireId(ExperimentAction.RecordVerdict, id) ?? RequireVerdict(evidence, verdict);

  private static DomainError? RequireVerdict(string? evidence, ExperimentVerdict? verdict) =>
      verdict is null
        ? new DomainError(ToolErrorCodes.MissingParameter,
            $"Missing required parameter '{VerdictName}'. record-verdict requires verdict: " +
            $"exactly one of {string.Join(", ", VerdictWords.Keys)}.")
        : RequireEvidence(evidence, verdict.Value);

  /// <summary>Evidence is required for a confirmed or refuted verdict and optional for an
  ///     inconclusive one — but a supplied evidence value must always be a clean line.</summary>
  private static DomainError? RequireEvidence(string? evidence, ExperimentVerdict verdict) =>
      verdict == ExperimentVerdict.Inconclusive
        ? OptionalLineValue(EvidenceName, evidence)
        : RequireValue(EvidenceName, evidence);

  private static DomainError? RequireId(ExperimentAction action, int? id) =>
      id is null
        ? new DomainError(ToolErrorCodes.MissingParameter,
            $"Missing required parameter '{IdName}'. {ActionText(action)} requires the id of the " +
            "record it references.")
        : null;

  private static DomainError? RequireValue(string name, string? value) =>
      value is null
        ? new DomainError(ToolErrorCodes.MissingParameter,
            $"Missing required parameter '{name}'.")
        : ValidateLineValue(name, value);

  private static DomainError? OptionalLineValue(string name, string? value) =>
      value is null ? null : ValidateLineValue(name, value);

  /// <summary>The ledger stores one line per record without padding, so every value that
  ///     becomes part of a line must already be a single, unpadded, non-empty line.</summary>
  private static DomainError? ValidateLineValue(string name, string value) =>
      value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal)
        ? new DomainError(ToolErrorCodes.InvalidParameterValue,
            $"'{name}' must be a single line: it may not contain a newline or carriage return.")
        : ValidateLinePadding(name, value);

  private static DomainError? ValidateLinePadding(string name, string value) =>
      value.Trim().Length == 0 ? EmptyValue(name) : PaddingError(name, value);

  private static DomainError EmptyValue(string name) =>
      new(ToolErrorCodes.InvalidParameterValue, $"'{name}' must be a non-empty string.");

  private static DomainError? PaddingError(string name, string value) =>
      value.Trim() != value
        ? new DomainError(ToolErrorCodes.InvalidParameterValue,
            $"'{name}' must not have leading or trailing whitespace (the ledger stores " +
            "single lines without padding).")
        : null;

  private static DomainError? FirstNonNull(params DomainError?[] errors) =>
      errors.FirstOrDefault(e => e is not null);

  /// <summary>The action's documented spelling, for error messages.</summary>
  internal static string ActionText(ExperimentAction action) => action switch
  {
    ExperimentAction.RecordHypothesis => "record-hypothesis",
    ExperimentAction.RecordPrediction => "record-prediction",
    ExperimentAction.RecordVerdict => "record-verdict",
    ExperimentAction.RecordObservation => "record-observation",
    ExperimentAction.RecordConclusion => "record-conclusion",
    ExperimentAction.RecordSkip => "record-skip",
    ExperimentAction.List => "list",
    ExperimentAction.Summary => "summary",
    // Unnamed enum values cannot occur.
    _ => throw new InvalidOperationException("Unknown experiment action."),
  };

  private static Result<ExperimentInput> Fail(DomainError err) => Result.Failure<ExperimentInput>(err);
}
