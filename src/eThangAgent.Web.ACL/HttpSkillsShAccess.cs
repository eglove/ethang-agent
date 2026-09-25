using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Web.ACL;

/// <summary>The skills.sh JSON API adapter (plan #29 task 8, ledger v57):
/// GET {base}/api/search?q=&lt;term&gt; returns the directory JSON for
/// SkillsShSearchParser. Same discipline as HttpWebAccess: redirects
/// followed, body size capped, transport failures as typed errors. The base
/// URI is injectable for tests; production wires https://www.skills.sh/.</summary>
public sealed class HttpSkillsShAccess(Uri? baseUrl = null, long maxBodyBytes = 2 * 1024 * 1024) : ISkillsShAccess, IDisposable
{
  private static readonly Uri ProductionBase = new("https://www.skills.sh");

  private readonly Uri _baseUrl = baseUrl ?? ProductionBase;
  private readonly long _maxBodyBytes = maxBodyBytes;
  private readonly HttpClient _client = new(new HttpClientHandler
  {
    AllowAutoRedirect = true,
    AutomaticDecompression = System.Net.DecompressionMethods.All,
    CheckCertificateRevocationList = true,
  })
  {
    Timeout = Timeout.InfiniteTimeSpan, // budget authority is the caller's token
  };

  public void Dispose() => _client.Dispose();

  public async Task<Result<string>> FetchSearchAsync(string query, CancellationToken ct = default)
  {
    Uri url = new(_baseUrl, "/api/search?q=" + Uri.EscapeDataString(query));
    try
    {
      using HttpRequestMessage request = new(HttpMethod.Get, url);
      request.Headers.UserAgent.ParseAdd("eThangAgent/1.0");
      using HttpResponseMessage response = await _client.SendAsync(
          request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
      if (!response.IsSuccessStatusCode)
      {
        return Result.Failure<string>(new DomainError("FetchFailed",
            $"skills.sh returned HTTP {(int)response.StatusCode} {response.ReasonPhrase} for '{query}'."));
      }

      long declared = response.Content.Headers.ContentLength ?? -1;
      if (declared > _maxBodyBytes)
      {
        return Result.Failure<string>(new DomainError("FetchFailed",
            $"skills.sh response declares {declared} bytes, over the {_maxBodyBytes}-byte cap."));
      }

      byte[] body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
      return body.LongLength > _maxBodyBytes
          ? Result.Failure<string>(new DomainError("FetchFailed",
              $"skills.sh response transferred {body.LongLength} bytes, over the {_maxBodyBytes}-byte cap."))
          : Result.Success(System.Text.Encoding.UTF8.GetString(body));
    }
    catch (HttpRequestException ex)
    {
      return Result.Failure<string>(new DomainError("FetchFailed", $"skills.sh request failed: {ex.Message}"));
    }
    catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
    {
      return Result.Failure<string>(new DomainError("FetchFailed", $"skills.sh request timed out: {ex.Message}"));
    }
    catch (InvalidOperationException ex)
    {
      return Result.Failure<string>(new DomainError("FetchFailed", $"skills.sh request was invalid: {ex.Message}"));
    }
  }
}