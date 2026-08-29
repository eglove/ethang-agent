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

    Result<string?> type = ToolArguments.OptionalString(json, TypeName);
    if (!type.IsSuccess)
    {
      return Fail(type.Error);
    }

    Result<string?> scope = ToolArguments.OptionalString(json, ScopeName);
    if (!scope.IsSuccess)
    {
      return Fail(scope.Error);
    }

    Result<string?> emojiKey = ToolArguments.OptionalString(json, EmojiKeyName);
    if (!emojiKey.IsSuccess)
    {
      return Fail(emojiKey.Error);
    }

    Result<string> description = ToolArguments.RequireString(json, DescriptionName, RequirementText);
    if (!description.IsSuccess)
    {
      return Fail(description.Error);
    }

    Result<string?> body = ToolArguments.OptionalString(json, BodyName);

    Result<IReadOnlyList<string>?> files = ToolArguments.OptionalStringArray(json, FilesName);
    if (!files.IsSuccess)
    {
      return Fail(files.Error);
    }

    CommitFilePaths? paths = null;
    if (files.Value is not null)
    {
      Result<CommitFilePaths> created = CommitFilePaths.Create(files.Value);
      if (!created.IsSuccess)
      {
        return Fail(created.Error);
      }

      paths = created.Value;
    }
    if (!body.IsSuccess)
    {
      return Fail(body.Error);
    }

    GitCommitInput input = new(type.Value, scope.Value, emojiKey.Value,
        description.Value, body.Value, paths);
    return Result.Success(input);
  }

  private static Result<GitCommitInput> Fail(DomainError err) =>
      Result.Failure<GitCommitInput>(err);
}
