using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

/// <summary>The model key must be READ LIVE at dispatch: a key saved mid-session
///     (Settings, API Keys — including one minted by sign-in) lands on the carrier and
///     the next request presents it, instead of the snapshot the container was built
///     with. Without a carrier the configuration's own key still applies.</summary>
public class OpenRouterModelKeyLiveTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");

  [Fact]
  public async Task KeySavedAfterConstruction_IsUsed_OnTheNextCall()
  {
    List<HttpRequestMessage> seen = [];
    FakeHttpMessageHandler handler = new(req =>
    {
      seen.Add(req);
      return Task.FromResult(Wire.Ok());
    });
    OpenRouterApiKeyCarrier carrier = new();
    // The snapshot config carries the OLD key — the pre-fix world where a key saved
    // later was invisible to an open session.
    OpenRouterModelProvider provider = new(new HttpClient(handler),
        new OpenRouterConfiguration("sk-or-v1-old", BaseUrl), keySource: carrier);
    ModelConfig config = ModelConfig.Create("openai/gpt-4o-mini", 128, 0.7f, 4096).Value!;

    _ = await provider.SendAsync(config, new ModelRequest([UserMsg("hi")]),
        TestContext.Current.CancellationToken);
    Assert.Equal("Bearer sk-or-v1-old", Assert.Single(seen).Headers.Authorization!.ToString());

    // Sign-in mints a new key mid-session: the carrier picks it up live.
    carrier.Current = "sk-or-v1-new";
    _ = await provider.SendAsync(config, new ModelRequest([UserMsg("hi")]),
        TestContext.Current.CancellationToken);
    Assert.Equal("Bearer sk-or-v1-new", seen[1].Headers.Authorization!.ToString());
  }

  [Fact]
  public async Task SnapshotConfigKey_StillWorks_WithoutACarrier()
  {
    HttpRequestMessage? captured = null;
    FakeHttpMessageHandler handler = new(req =>
    {
      captured = req;
      return Task.FromResult(Wire.Ok());
    });
    OpenRouterModelProvider provider = new(new HttpClient(handler),
        new OpenRouterConfiguration("sk-or-v1-snapshot", BaseUrl));

    _ = await provider.SendAsync(ModelConfig.Create("openai/gpt-4o-mini", 128, 0.7f, 4096).Value!,
        new ModelRequest([UserMsg("hi")]), TestContext.Current.CancellationToken);

    Assert.Equal("Bearer sk-or-v1-snapshot", captured!.Headers.Authorization!.ToString());
  }

  private static Message UserMsg(string text) => new(Role.User, text, DateTimeOffset.UtcNow);
}
