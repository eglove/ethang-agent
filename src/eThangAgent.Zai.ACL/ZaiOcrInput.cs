using System.Text.Json;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Zai.ACL;

public sealed record ZaiOcrInput(string Path, int? StartPage, int? EndPage)
{
  internal const string AllowedList = "path, startPage, endPage, timeoutSeconds";
  internal const long ImageByteLimit = 10 * 1024 * 1024;
  internal const long PdfByteLimit = 50 * 1024 * 1024;

  public static Result<ZaiOcrInput> Create(JsonElement json)
  {
    DomainError? unknown = ZaiToolInput.RejectUnknown(json, AllowedList, "path", "startPage", "endPage");
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
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue, "'path' must be a non-empty string."));
    }
    string path = pathEl.GetString()!;
    bool isPdf = path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    bool isImage = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
    if (!isPdf && !isImage)
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'path' must reference a .pdf, .jpg, or .png document (got \"{path}\")."));
    }

    int? startPage = ParsePage(json, "startPage");
    int? endPage = ParsePage(json, "endPage");
    if (startPage is 0 || endPage is 0)
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "'startPage' and 'endPage' must be integers ≥ 1 when present."));
    }

    if (startPage is { } start && endPage is { } end && end < start)
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'endPage' ({end}) must not be below 'startPage' ({start})."));
    }

    ZaiOcrInput input = new(path, startPage, endPage);
    return Result.Success(input);
  }

  /// <summary>Parses an optional page bound; 0 signals a parse/range violation.</summary>
  private static int? ParsePage(JsonElement json, string name)
  {
    if (!json.TryGetProperty(name, out JsonElement el))
    {
      return null;
    }

    int? page = el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int value) ? value : null;
    return page is >= 1 ? page : 0;
  }

  private static Result<ZaiOcrInput> Missing(string n) =>
      Result.Failure<ZaiOcrInput>(new DomainError("MissingParameter",
          $"Missing required parameter '{n}'. This tool requires path."));

  private static Result<ZaiOcrInput> Fail(DomainError err) =>
      Result.Failure<ZaiOcrInput>(err);
}
