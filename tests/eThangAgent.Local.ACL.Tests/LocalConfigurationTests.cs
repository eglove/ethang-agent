namespace eThangAgent.Local.ACL.Tests;

public class LocalConfigurationTests
{
  [Fact]
  public void ChatEndpoint_AppendsChatPath_PreservingBasePath()
  {
    LocalConfiguration config = new(new Uri("http://localhost:1234/v1"));
    Assert.Equal("http://localhost:1234/v1/chat/completions", config.ChatCompletionsEndpoint().ToString());
  }

  [Fact]
  public void ChatEndpoint_TrailingSlashOnBaseUrl_DoesNotDoubleSlash()
  {
    LocalConfiguration config = new(new Uri("http://localhost:1234/"));
    Assert.Equal("http://localhost:1234/chat/completions", config.ChatCompletionsEndpoint().ToString());
  }

  // CA1054: InlineData requires compile-time string constants; a System.Uri parameter
  // cannot receive them. The strings are turned into Uris inside the test body.
#pragma warning disable CA1054
  [Theory]
  [InlineData("http://localhost:1234", "/v1/models", "http://localhost:1234/v1/models")]
  [InlineData("http://localhost:1234/v1", "/chat/completions", "http://localhost:1234/v1/chat/completions")]
  public void Endpoint_PathAppend_Holds(string baseUrl, string path, string expected)
#pragma warning restore CA1054
  {
    LocalConfiguration config = new(new Uri(baseUrl));
    Assert.Equal(expected, config.Endpoint(path).ToString());
  }

  [Fact]
  public void ApiKey_IsOptional()
  {
    LocalConfiguration config = new(new Uri("http://localhost:1234"));
    Assert.Null(config.ApiKey);
  }

  [Fact]
  public void Retry_DefaultsToDefaultPolicy()
  {
    LocalConfiguration config = new(new Uri("http://localhost:1234"));
    Assert.Equal(RetryPolicy.Default.MaxAttempts, config.Retry.MaxAttempts);
  }
}
