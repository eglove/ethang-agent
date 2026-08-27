using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.Zai.ACL;

public sealed record ZaiTranscriptionInput(string Path, string? Context)
{
  internal const string AllowedList = "path, context, timeoutSeconds";
  internal const long ByteLimit = 25 * 1024 * 1024;
  internal const int ContextLimit = 8000;

  public static Result<ZaiTranscriptionInput> Create(JsonElement json)
  {
    DomainError? unknown = ZaiToolInput.RejectUnknown(json, AllowedList, "path", "context");
    if (unknown is not null)
    {
      return Fail(unknown);
    }

    if (!json.TryGetProperty("path", out JsonElement pathEl))
    {
      return Missing("path");
    }
    if (pathEl.ValueKind != JsonValueKind.String || pathEl.GetString()!.Length == 0)
    {
      return Fail(new DomainError("InvalidParameterValue", "'path' must be a non-empty string."));
    }
    string path = pathEl.GetString()!;
    if (!path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
        && !path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
    {
      return Fail(new DomainError("InvalidParameterValue",
          $"'path' must reference a .wav or .mp3 audio file (got \"{path}\")."));
    }

    string? context = null;
    if (json.TryGetProperty("context", out JsonElement contextEl))
    {
      if (contextEl.ValueKind != JsonValueKind.String)
      {
        return Fail(new DomainError("InvalidParameterType",
            $"'context' must be a string, but got {contextEl.ValueKind}."));
      }
      context = contextEl.GetString();
      if (context!.Length > ContextLimit)
      {
        return Fail(new DomainError("InvalidParameterValue",
            $"'context' must be at most {ContextLimit} characters (got {context.Length})."));
      }
    }

    return Result.Success(new ZaiTranscriptionInput(path, context));
  }

  private static Result<ZaiTranscriptionInput> Missing(string n) =>
      Result.Failure<ZaiTranscriptionInput>(new DomainError("MissingParameter",
          $"Missing required parameter '{n}'. This tool requires path."));

  private static Result<ZaiTranscriptionInput> Fail(DomainError err) =>
      Result.Failure<ZaiTranscriptionInput>(err);
}
