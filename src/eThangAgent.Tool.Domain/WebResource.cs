namespace eThangAgent.ToolDomain;

/// <summary>A fetched web resource. <see cref="Url" /> is the final URL after any
///     redirects — callers must report it, not the originally requested one. The ACL
///     decodes the body to text (honoring the response charset, defaulting UTF-8) and
///     reports the transferred byte length separately, so the domain never holds raw
///     bytes; binary content is rejected by the ACL, which knows the actual encoding
///     state of the response.</summary>
public sealed record WebResource(
    Uri Url,
    int StatusCode,
    string ReasonPhrase,
    string ContentType,
    string Body,
    long ByteCount);
