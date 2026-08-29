using System.Net;
using System.Text;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

public class OpenRouterModelProviderFactoryTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");

  [Fact]
  public async Task Create_PerSpawnModel_ReachesWireWithBaseCredentials()
  {
    HttpRequestMessage? captured = null;
    string? body = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      captured = req;
      Assert.NotNull(req.Content);
      body = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent(
                                       /*lang=json,strict*/
                                       """{"choices":[{"message":{"content":"ok"}}]}""",
                  Encoding.UTF8,
                  "application/json"),
      };
    });
    using HttpClient http = new(handler);
    OpenRouterModelProviderFactory factory = new(
        new OpenRouterConfiguration("test-key", BaseUrl), http);
    IModelProvider provider = factory.Create(ModelConfig.Create("mock/sub-model", null, 128, 0.7f).Value!);

    Result<ModelResponse> result = await provider.SendAsync(
        ModelConfig.Create("mock/sub-model", null, 128, 0.7f).Value!,
        new ModelRequest([new Message(Role.User, "hi", DateTimeOffset.UtcNow)]));

    Assert.True(result.IsSuccess);
    Assert.Contains("\"model\":\"mock/sub-model\"", body, StringComparison.Ordinal);
    Assert.Equal("Bearer test-key", captured!.Headers.Authorization!.ToString());
  }

  [Fact]
  public void Create_NullConfig_Throws()
  {
    using HttpClient http = new();
    OpenRouterModelProviderFactory factory = new(
        new OpenRouterConfiguration("k", BaseUrl), http);

    _ = Assert.Throws<ArgumentNullException>(() => factory.Create(null!));
  }
}
