using System.Net;
using eThangAgent.ModelDomain;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.Local.ACL.Tests;

public class LocalModelProviderFactoryTests
{
  private static readonly Uri BaseUrl = new("http://localhost:1234/v1");

  [Fact]
  public void Create_ReturnsProviderSharingConfig()
  {
    using HttpClient http = new(new FakeHttpMessageHandler(_ =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
    LocalModelProviderFactory factory = new(new LocalConfiguration(BaseUrl), http);

    IModelProvider first = factory.Create(ModelConfig.Create("local-model", null, 128, 0.7f, 4096).Value!);
    IModelProvider second = factory.Create(ModelConfig.Create("other-local-model", null, 64, 0.5f, 8_192).Value!);

    Assert.NotNull(first);
    Assert.NotNull(second);
    _ = Assert.IsType<LocalModelProvider>(first);
    _ = Assert.IsType<LocalModelProvider>(second);
  }

  [Fact]
  public void Create_NullConfig_Throws()
  {
    using HttpClient http = new(new FakeHttpMessageHandler(_ =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
    LocalModelProviderFactory factory = new(new LocalConfiguration(BaseUrl), http);

    _ = Assert.Throws<ArgumentNullException>(() => factory.Create(null!));
  }
}
