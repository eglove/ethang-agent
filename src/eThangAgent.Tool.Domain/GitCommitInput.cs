using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>
///     Shape-only parsing for git_commit. Description is the only required key;
///     type, scope, emoji_key, and body are optional. The commit style is NOT
///     model input — the tool resolves it from the host's
///     <see cref="ICommitStyleProvider"/> at execution time, so a stale caller
///     naming 'style' is rejected here as unknown input. All semantic rules
///     (style legality, type sets, emoji lookup, length limits) belong to
///     <see cref="CommitMessage.Create"/> — their error codes surface verbatim.
/// </summary>
public sealed record GitCommitInput(
    string? Type, string? Scope, string? EmojiKey,
    string Description, string? Body, CommitFilePaths? Files)
{
  private const string TypeName = "type";
  private const string ScopeName = "scope";
  private const string EmojiKeyName = "emoji_key";
  private const string DescriptionName = "description";
  private const string BodyName = "body";
  private const string RequirementText = "This tool requires description.";

  private const string FilesName = "files";
  private static readonly string[] AllowedNames =
    [TypeName, ScopeName, EmojiKeyName, DescriptionName, BodyName,
        FilesName, ToolTimeout.ParameterName];

  public static Result<GitCommitInput> Create(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Fail(baseParse.Error);
    }

    JsonElement json = baseParse.Value;
    DomainError? unknown = ToolArguments.RejectUnknownParameters(json, AllowedNames);
    if (unknown is not null)
    {
      return Fail(unknown);
    }

    Result<OptionalFields> optional = ParseOptional(json);
    if (!optional.IsSuccess)
    {
      return Fail(optional.Error);
    }

    Result<string> description = ToolArguments.RequireString(json, DescriptionName, RequirementText);
    if (!description.IsSuccess)
    {
      return Fail(description.Error);
    }

    Result<string?> body = ToolArguments.OptionalString(json, BodyName);
    Result<CommitFilePaths?> paths = ParseFiles(ToolArguments.OptionalStringArray(json, FilesName));
    if (!paths.IsSuccess)
    {
      return Fail(paths.Error);
    }

    if (!body.IsSuccess)
    {
      return Fail(body.Error);
    }

    GitCommitInput input = new(optional.Value.Type, optional.Value.Scope, optional.Value.EmojiKey,
        description.Value, body.Value, paths.Value);
    return Result.Success(input);
  }

  private sealed record OptionalFields(string? Type, string? Scope, string? EmojiKey);

  private static Result<OptionalFields> ParseOptional(JsonElement json)
  {
    Result<string?> type = ToolArguments.OptionalString(json, TypeName);
    if (!type.IsSuccess)
    {
      return Result.Failure<OptionalFields>(type.Error);
    }

    Result<string?> scope = ToolArguments.OptionalString(json, ScopeName);
    if (!scope.IsSuccess)
    {
      return Result.Failure<OptionalFields>(scope.Error);
    }

    Result<string?> emojiKey = ToolArguments.OptionalString(json, EmojiKeyName);
    return emojiKey.IsSuccess
      ? Result.Success(new OptionalFields(type.Value, scope.Value, emojiKey.Value))
      : Result.Failure<OptionalFields>(emojiKey.Error);
  }

  private static Result<CommitFilePaths?> ParseFiles(Result<IReadOnlyList<string>?> files)
  {
    if (!files.IsSuccess)
    {
      return Result.Failure<CommitFilePaths?>(files.Error);
    }

    if (files.Value is null)
    {
      return Result.Success<CommitFilePaths?>(null);
    }

    Result<CommitFilePaths> created = CommitFilePaths.Create(files.Value);
    return created.IsSuccess
      ? Result.Success<CommitFilePaths?>(created.Value)
      : Result.Failure<CommitFilePaths?>(created.Error);
  }

  private static Result<GitCommitInput> Fail(DomainError err) =>
      Result.Failure<GitCommitInput>(err);
}
