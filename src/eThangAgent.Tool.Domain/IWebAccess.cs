using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Fetch access for web resources over HTTP(S). The implementation follows
///     redirects; transport-level failures (DNS, TLS, unreachable host, body over the
///     implementation's size cap, or a non-decodable body) are typed errors — never
///     exceptions. Decoding to text is the ACL's job: it knows the response charset.</summary>
public interface IWebAccess
{
  /// <summary>Fetches the resource at <paramref name="url" />, following redirects.
  ///     Success carries the FINAL url after redirects, the status code, reason
  ///     phrase, content type, the body decoded to text, and the transferred byte
  ///     count. Binary (non-decodable-text) content types are a typed failure.</summary>
  Task<Result<WebResource>> FetchAsync(Uri url, CancellationToken ct = default);
}
