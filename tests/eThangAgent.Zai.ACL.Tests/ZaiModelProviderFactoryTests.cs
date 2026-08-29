using System.Net;
using System.Text;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.Zai.ACL.Tests;

public class ZaiModelProviderFactoryTests
{
  private static readonly Uri BaseUrl = new("https://zai.test");

  [Fact]
  public async Task Create_PerSpawnModel_ReachesWireWithSharedCredential()
  {
    string? capturedBody = null;
    HttpRequestMessage? captured = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      captured = req;
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent(
              /*lang=json,strict*/
              """{"choices":[{"message":{"content":"ok"}}]}""", Encoding.UTF8, "application/json")
      };
    });
    using HttpClient http = new(handler);
    ZaiModelProviderFactory factory = new(new ZaiConfiguration("shared-key", BaseUrl), http);

    IModelProvider provider = factory.Create(ModelConfig.Create("glm-4.5-air", null, 64, 0.5f).Value!);
    _ = await provider.SendAsync(
        ModelConfig.Create("glm-4.5-air", null, 64, 0.5f).Value!,
        new ModelRequest([new Message(Role.User, "hi", DateTimeOffset.UtcNow)]), TestContext.Current.CancellationToken);

    Assert.Equal("Bearer shared-key", captured!.Headers.Authorization?.ToString());
    Assert.Contains("glm-4.5-air", capturedBody, StringComparison.Ordinal);
  }

  [Fact]
  public void Create_NullConfig_Throws()
  {
    using HttpClient http = new(new FakeHttpMessageHandler(_ =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
    ZaiModelProviderFactory factory = new(new ZaiConfiguration("k", BaseUrl), http);

    _ = Assert.Throws<ArgumentNullException>(() => factory.Create(null!));
  }
}
