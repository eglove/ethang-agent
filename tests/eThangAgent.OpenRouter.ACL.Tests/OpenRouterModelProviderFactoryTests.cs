using System.Net;
using System.Text;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;

namespace eThangAgent.OpenRouter.ACL.Tests;

public class OpenRouterModelProviderFactoryTests
{
    private static readonly Uri BaseUrl = new("https://openrouter.test");

    [Fact]
    public async Task Create_PerSpawnModel_ReachesWireWithBaseCredentials()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var handler = new FakeHttpMessageHandler(async req =>
        {
            captured = req;
            body = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"content":"ok"}}]}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        using var http = new HttpClient(handler);
        var factory = new OpenRouterModelProviderFactory(
            new OpenRouterConfiguration("test-key", BaseUrl), http);
        var provider = factory.Create(ModelConfig.Create("mock/sub-model", 128, 0.7f).Value!);

        var result = await provider.SendAsync(
            ModelConfig.Create("mock/sub-model", 128, 0.7f).Value!,
            new ModelRequest([new Message(Role.User, "hi", DateTimeOffset.UtcNow)]));

        Assert.True(result.IsSuccess);
        Assert.Contains("\"model\":\"mock/sub-model\"", body);
        Assert.Equal("Bearer test-key", captured!.Headers.Authorization!.ToString());
    }

    [Fact]
    public void Create_NullConfig_Throws()
    {
        using var http = new HttpClient();
        var factory = new OpenRouterModelProviderFactory(
            new OpenRouterConfiguration("k", BaseUrl), http);

        Assert.Throws<ArgumentNullException>(() => factory.Create(null!));
    }
}