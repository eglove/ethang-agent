using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>
///     Shape-only parsing for git_commit. Style and description are required keys;
///     type, scope, emoji_key, and body are optional. All semantic rules (style
///     legality, type sets, emoji lookup, length limits) belong to
///     <see cref="CommitMessage.Create"/> — their error codes surface verbatim.
/// </summary>
public sealed record GitCommitInput(
    string Style, string? Type, string? Scope, string? EmojiKey,
    string Description, string? Body)
{
  private const string StyleName = "style";
  private const string TypeName = "type";
  private const string ScopeName = "scope";
  private const string EmojiKeyName = "emoji_key";
  private const string DescriptionName = "description";
  private const string BodyName = "body";
  private const string RequirementText = "This tool requires style and description.";

  private static readonly string[] AllowedNames =
      [StyleName, TypeName, ScopeName, EmojiKeyName, DescriptionName, BodyName, ToolTimeout.ParameterName];

  public static Result<GitCommitInput> Create(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Fail(baseParse.Error!);
    }

    JsonElement json = baseParse.Value;
    DomainError? unknown = ToolArguments.RejectUnknownParameters(json, AllowedNames);
    if (unknown is not null)
    {
      return Fail(unknown);
    }

    Result<string> style = ToolArguments.RequireString(json, StyleName, RequirementText);
    if (!style.IsSuccess)
    {
      return Fail(style.Error!);
    }

    Result<string?> type = ToolArguments.OptionalString(json, TypeName);
    if (!type.IsSuccess)
    {
      return Fail(type.Error!);
    }

    Result<string?> scope = ToolArguments.OptionalString(json, ScopeName);
    if (!scope.IsSuccess)
    {
      return Fail(scope.Error!);
    }

    Result<string?> emojiKey = ToolArguments.OptionalString(json, EmojiKeyName);
    if (!emojiKey.IsSuccess)
    {
      return Fail(emojiKey.Error!);
    }

    Result<string> description = ToolArguments.RequireString(json, DescriptionName, RequirementText);
    if (!description.IsSuccess)
    {
      return Fail(description.Error!);
    }

    Result<string?> body = ToolArguments.OptionalString(json, BodyName);
    if (!body.IsSuccess)
    {
      return Fail(body.Error!);
    }

    GitCommitInput input = new(style.Value!, type.Value, scope.Value, emojiKey.Value, description.Value!, body.Value);
    return Result.Success(input);
  }

  private static Result<GitCommitInput> Fail(DomainError err) =>
      Result.Failure<GitCommitInput>(err);
}
