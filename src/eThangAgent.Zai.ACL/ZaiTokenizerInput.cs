using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.Zai.ACL;

public sealed record ZaiTokenizerInput(string Model, string Text)
{
  internal const string AllowedList = "model, text, timeoutSeconds";
  internal const int TextLimit = 400_000;

  /// <summary>The tokenizer's supported model set; glm-4.6 is the documented default.</summary>
  internal static readonly string[] Models = ["glm-4.6", "glm-4.6v", "glm-4.5"];

  public static Result<ZaiTokenizerInput> Create(JsonElement json)
  {
    DomainError? unknown = ZaiToolInput.RejectUnknown(json, AllowedList, "model", "text");
    if (unknown is not null)
    {
      return Fail(unknown);
    }

    string model = "glm-4.6";
    if (json.TryGetProperty("model", out JsonElement modelEl))
    {
      if (modelEl.ValueKind != JsonValueKind.String)
      {
        return WrongType("model", "string", modelEl.ValueKind);
      }
      model = modelEl.GetString()!;
      if (!Models.Contains(model, StringComparer.Ordinal))
      {
        return Fail(new DomainError("InvalidParameterValue",
            $"'model' must be one of {string.Join(", ", Models)} (got \"{model}\")."));
      }
    }

    if (!json.TryGetProperty("text", out JsonElement textEl))
    {
      return Missing("text");
    }
    if (textEl.ValueKind != JsonValueKind.String)
    {
      return WrongType("text", "string", textEl.ValueKind);
    }
    string text = textEl.GetString()!;
    if (text.Length == 0)
    {
      return Fail(new DomainError("InvalidParameterValue", "'text' must be a non-empty string."));
    }

    if (text.Length > TextLimit)
    {
      return Fail(new DomainError("InvalidParameterValue",
          $"'text' exceeds {TextLimit} characters ({text.Length}); count a smaller piece."));
    }

    ZaiTokenizerInput input = new(model, text);
    return Result.Success(input);
  }

  private static Result<ZaiTokenizerInput> Missing(string n) =>
      Result.Failure<ZaiTokenizerInput>(new DomainError("MissingParameter",
          $"Missing required parameter '{n}'. This tool requires text."));

  private static Result<ZaiTokenizerInput> WrongType(string n, string e, JsonValueKind a) =>
      Result.Failure<ZaiTokenizerInput>(new DomainError("InvalidParameterType",
          $"'{n}' must be a {e}, but got {a}."));

  private static Result<ZaiTokenizerInput> Fail(DomainError err) =>
      Result.Failure<ZaiTokenizerInput>(err);
}
