namespace eThangAgent.ToolDomain;

/// <summary>Converts an HTML document into readable markdown. The <c>baseUrl</c>
///     argument is the document's final URL; relative links and image sources are
///     resolved against it. Conversion is a pure function of its inputs.</summary>
public interface IHtmlToMarkdown
{
  string Convert(string html, Uri baseUrl);
}
