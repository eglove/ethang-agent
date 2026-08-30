using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Web.ACL;

/// <summary>Real HTTP(S) fetch over HttpClient: follows redirects, enforces a body
///     size cap, decodes the response per its declared charset, and reports the
///     transferred byte count. Binary content types are a typed error — the tool
///     contract is textual content only. Transport failures (DNS, TLS, refused,
///     too large) surface as typed errors, never exceptions.</summary>
public sealed class HttpWebAccess : IWebAccess, IDisposable
{
  /// <summary>Upper bound on downloaded bodies. Oversized transfers are aborted.</summary>
  private const long MaxBodyBytes = 10 * 1024 * 1024;

  private readonly HttpClient _client = new(new HttpClientHandler
  {
    AllowAutoRedirect = true,
    AutomaticDecompression = System.Net.DecompressionMethods.All,
    CheckCertificateRevocationList = true,
  })
  {
    Timeout = Timeout.InfiniteTimeSpan, // budget authority is the tool's timeoutSeconds token
  };

  public void Dispose() => _client.Dispose();

  public async Task<Result<WebResource>> FetchAsync(Uri url, CancellationToken ct = default)
  {
    try
    {
      using HttpRequestMessage request = new(HttpMethod.Get, url);
      request.Headers.UserAgent.ParseAdd("eThangAgent/1.0");
      using HttpResponseMessage response = await _client.SendAsync(
          request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

      string contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
      if (!IsTextual(contentType))
      {
        return Result.Failure<WebResource>(new DomainError("UnsupportedMediaType",
            $"'{url}' responded with '{contentType}' (HTTP {(int)response.StatusCode} {response.ReasonPhrase}). " +
            "Only textual content is returned; binary bodies are rejected."));
      }

      long declared = response.Content.Headers.ContentLength ?? -1;
      if (declared > MaxBodyBytes)
      {
        return Result.Failure<WebResource>(new DomainError("BodyTooLarge",
            $"'{url}' declares {declared} bytes, over the {MaxBodyBytes}-byte fetch cap."));
      }

      byte[] body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
      if (body.LongLength > MaxBodyBytes)
      {
        return Result.Failure<WebResource>(new DomainError("BodyTooLarge",
            $"'{url}' transferred {body.LongLength} bytes, over the {MaxBodyBytes}-byte fetch cap."));
      }

      string text = Decode(body, contentType);
      return Result.Success(new WebResource(
          response.RequestMessage!.RequestUri ?? url,
          (int)response.StatusCode,
          response.ReasonPhrase ?? string.Empty,
          contentType,
          text,
          body.LongLength));
    }
    catch (OperationCanceledException)
    {
      throw; // the tool layer converts an elapsed budget into Error [ToolTimeout]
    }
    catch (HttpRequestException ex)
    {
      return Result.Failure<WebResource>(new DomainError("FetchFailed",
          $"Failed to fetch '{url}': {ex.Message}"));
    }
    catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
    {
      return Result.Failure<WebResource>(new DomainError("FetchFailed",
          $"Failed to fetch '{url}': {ex.Message}"));
    }
  }

  private static bool IsTextual(string contentType)
  {
    string media = contentType.Split(';')[0].Trim();
    return media.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || media.EndsWith("+xml", StringComparison.OrdinalIgnoreCase)
        || media is "application/json" or "application/xml" or "application/javascript"
            or "application/yaml" or "application/x-yaml" or "application/xhtml+xml";
  }

  /// <summary>Decodes honoring a charset parameter when present; defaults to UTF-8,
  ///     falling back to replacement-safe UTF-8 for unknown charset names.</summary>
  private static string Decode(byte[] body, string contentType)
  {
    foreach (string part in contentType.Split(';'))
    {
      string trimmed = part.Trim();
      if (trimmed.StartsWith("charset=", StringComparison.OrdinalIgnoreCase))
      {
        string name = trimmed["charset=".Length..].Trim(' ', '"');
        try
        {
          return System.Text.Encoding.GetEncoding(name).GetString(body);
        }
        catch (ArgumentException)
        {
          return System.Text.Encoding.UTF8.GetString(body);
        }
      }
    }

    return System.Text.Encoding.UTF8.GetString(body);
  }
}
