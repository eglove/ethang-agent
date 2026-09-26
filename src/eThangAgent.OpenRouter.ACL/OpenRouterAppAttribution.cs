namespace eThangAgent.OpenRouter.ACL;

/// <summary>App attribution (https://openrouter.ai/docs/app-attribution): the three
///     headers that identify this app to OpenRouter — HTTP-Referer (the required
///     identifier), X-OpenRouter-Title (the display name), and
///     X-OpenRouter-Categories (the marketplace category). Hosts apply them ONCE to
///     their shared HttpClient; every provider and catalog request then carries them.
///     Apply is idempotent: a second call never duplicates a header.</summary>
public static class OpenRouterAppAttribution
{
  private const string Referer = "https://github.com/eglove/ethang-agent";
  private const string Title = "eThang Agent";
  private const string Categories = "programming-app";

  /// <summary>Stamps the attribution headers onto the client's default request
  ///     headers. Safe to call repeatedly (Remove before Add).</summary>
  public static void Apply(HttpClient http)
  {
    ArgumentNullException.ThrowIfNull(http);
    _ = http.DefaultRequestHeaders.Remove("HTTP-Referer");
    http.DefaultRequestHeaders.Add("HTTP-Referer", Referer);
    _ = http.DefaultRequestHeaders.Remove("X-OpenRouter-Title");
    http.DefaultRequestHeaders.Add("X-OpenRouter-Title", Title);
    _ = http.DefaultRequestHeaders.Remove("X-OpenRouter-Categories");
    http.DefaultRequestHeaders.Add("X-OpenRouter-Categories", Categories);
  }
}
