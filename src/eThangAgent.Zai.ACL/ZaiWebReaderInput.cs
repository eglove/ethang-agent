using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.Zai.ACL;

public sealed record ZaiWebReaderInput(Uri Url)
{
  internal const string AllowedList = "url, timeoutSeconds";

  public static Result<ZaiWebReaderInput> Create(JsonElement json)
  {
    DomainError? unknown = ZaiToolInput.RejectUnknown(json, AllowedList, "url");
    if (unknown is not null)
    {
      return Fail(unknown);
    }

    if (!json.TryGetProperty("url", out JsonElement urlEl))
    {
      return Missing("url");
    }
    if (urlEl.ValueKind != JsonValueKind.String)
    {
      return WrongType("url", "string", urlEl.ValueKind);
    }
    string url = urlEl.GetString()!;
    return !Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
        || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
      ? Fail(new DomainError("InvalidParameterValue",
          $"'url' must be an absolute http(s) URL (got \"{url}\")."))
      : Result.Success(new ZaiWebReaderInput(parsed));
  }

  private static Result<ZaiWebReaderInput> Missing(string n) =>
      Result.Failure<ZaiWebReaderInput>(new DomainError("MissingParameter",
          $"Missing required parameter '{n}'. This tool requires url."));

  private static Result<ZaiWebReaderInput> WrongType(string n, string e, JsonValueKind a) =>
      Result.Failure<ZaiWebReaderInput>(new DomainError("InvalidParameterType",
          $"'{n}' must be a {e}, but got {a}."));

  private static Result<ZaiWebReaderInput> Fail(DomainError err) =>
      Result.Failure<ZaiWebReaderInput>(err);
}
