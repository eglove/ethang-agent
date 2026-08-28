using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.Zai.ACL;

public sealed record ZaiImageInput(string Prompt, string Filename, string Size)
{
  internal const string AllowedList = "prompt, filename, size, timeoutSeconds";
  internal const int DimensionMin = 1024;
  internal const int DimensionMax = 2048;

  private const string FilenameName = "filename";

  public static Result<ZaiImageInput> Create(JsonElement json)
  {
    DomainError? unknown = ZaiToolInput.RejectUnknown(json, AllowedList, "prompt", FilenameName, "size");
    if (unknown is not null)
    {
      return Fail(unknown);
    }

    if (!json.TryGetProperty("prompt", out JsonElement promptEl))
    {
      return Missing("prompt");
    }
    if (promptEl.ValueKind != JsonValueKind.String || promptEl.GetString()!.Length == 0)
    {
      return Fail(new DomainError("InvalidParameterValue", "'prompt' must be a non-empty string."));
    }
    string prompt = promptEl.GetString()!;

    if (!json.TryGetProperty(FilenameName, out JsonElement fileEl))
    {
      return Missing(FilenameName);
    }
    if (fileEl.ValueKind != JsonValueKind.String)
    {
      return WrongType(FilenameName, "string", fileEl.ValueKind);
    }
    string filename = fileEl.GetString()!;
    if (!filename.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
    {
      return Fail(new DomainError("InvalidParameterValue",
          $"'filename' must end in .png (got \"{filename}\")."));
    }

    string size = "1280x1280";
    if (json.TryGetProperty("size", out JsonElement sizeEl))
    {
      if (sizeEl.ValueKind != JsonValueKind.String)
      {
        return WrongType("size", "string", sizeEl.ValueKind);
      }
      size = sizeEl.GetString()!;
      if (!TryParseSize(size, out _))
      {
        return Fail(new DomainError("InvalidParameterValue",
            $"'size' must be '<width>x<height>' with both dimensions {DimensionMin}..{DimensionMax} " +
            $"and divisible by 32 (got \"{size}\")."));
      }
    }

    return Result.Success(new ZaiImageInput(prompt, filename, size));
  }

  /// <summary>glm-image size rules: both dimensions 1024..2048, divisible by 32.</summary>
  internal static bool TryParseSize(string size, out (int Width, int Height) parsed)
  {
    parsed = default;
    string[] parts = size.Split('x');
    if (parts.Length != 2
        || !int.TryParse(parts[0], out int width) || !int.TryParse(parts[1], out int height))
    {
      return false;
    }

    if (width is < DimensionMin or > DimensionMax || height is < DimensionMin or > DimensionMax
        || width % 32 != 0 || height % 32 != 0)
    {
      return false;
    }

    parsed = (width, height);
    return true;
  }

  private static Result<ZaiImageInput> Missing(string n) =>
      Result.Failure<ZaiImageInput>(new DomainError("MissingParameter",
          $"Missing required parameter '{n}'. This tool requires prompt and filename."));

  private static Result<ZaiImageInput> WrongType(string n, string e, JsonValueKind a) =>
      Result.Failure<ZaiImageInput>(new DomainError("InvalidParameterType",
          $"'{n}' must be a {e}, but got {a}."));

  private static Result<ZaiImageInput> Fail(DomainError err) =>
      Result.Failure<ZaiImageInput>(err);
}
