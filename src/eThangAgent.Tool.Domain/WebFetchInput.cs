using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Validated input for the web_fetch tool: an absolute http/https URL
///     plus the shared mandatory execution budget. Schemes outside http/https are
///     rejected — the tool fetches web resources, never local files or arbitrary
///     URI handlers.</summary>
public sealed record WebFetchInput(Uri Url)
{
  private const string UrlName = "url";
  private const string RequiredParamsText = "This tool requires url and timeoutSeconds.";

  private static readonly string[] AllowedNames = [UrlName, ToolTimeout.ParameterName];

  public static Result<WebFetchInput> Create(string jsonArguments)
  {
    return ToolArguments.ParseObject(jsonArguments)
        .Bind(json =>
        {
          DomainError? unknown = ToolArguments.RejectUnknownParameters(json, AllowedNames);
          return unknown is not null
            ? Result.Failure<JsonElement>(unknown)
            : Result.Success(json);
        })

        .Bind(ParseUrl)
        .Map(url => new WebFetchInput(url));
  }

  private static Result<Uri> ParseUrl(JsonElement json) =>
      ToolArguments.RequireString(json, UrlName, RequiredParamsText)
          .Bind(text => Uri.TryCreate(text, UriKind.Absolute, out Uri? parsed)
              && parsed.Scheme is "http" or "https"
            ? Result.Success(parsed)
            : Result.Failure<Uri>(new DomainError(ToolErrorCodes.InvalidParameterValue,
                $"'url' must be an absolute http/https URL (got '{text}'). " +
                "Other schemes — file, ftp, javascript, data, and so on — are rejected.")));
}
