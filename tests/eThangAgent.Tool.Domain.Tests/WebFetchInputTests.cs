using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Input-contract tests for the web_fetch tool: strict URL validation
///     (absolute http/https only), unknown-parameter rejection, and the mandatory
///     timeoutSeconds budget — nothing coerced or defaulted.</summary>
public class WebFetchInputTests
{
  [Fact]
  public void RelativeUrl_IsRejected()
  {
    Result<WebFetchInput> parsed = WebFetchInput.Create(
        /*lang=json,strict*/ """{"url":"docs/page.html","timeoutSeconds":30}""");
    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidParameterValue", parsed.Error.Code);
  }

  [Fact]
  public void HttpsUrl_IsAccepted()
  {
    Result<WebFetchInput> parsed = WebFetchInput.Create(
        /*lang=json,strict*/ """{"url":"https://example.com/page","timeoutSeconds":30}""");
    Assert.True(parsed.IsSuccess);
    Assert.Equal("https://example.com/page", parsed.Value.Url.ToString());
  }

  [Fact]
  public void HttpUrl_IsAccepted()
  {
    Result<WebFetchInput> parsed = WebFetchInput.Create(
        /*lang=json,strict*/ """{"url":"http://example.com/","timeoutSeconds":30}""");
    Assert.True(parsed.IsSuccess);
  }

  [Fact]
  public void FileScheme_IsRejected()
  {
    Result<WebFetchInput> parsed = WebFetchInput.Create(
        /*lang=json,strict*/ """{"url":"file:///C:/Windows/system.ini","timeoutSeconds":30}""");
    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidParameterValue", parsed.Error.Code);
  }

  [Fact]
  public void FtpScheme_IsRejected()
  {
    Result<WebFetchInput> parsed = WebFetchInput.Create(
        /*lang=json,strict*/ """{"url":"ftp://example.com/x","timeoutSeconds":30}""");
    Assert.False(parsed.IsSuccess);
  }

  [Fact]
  public void JavaScriptScheme_IsRejected()
  {
    Result<WebFetchInput> parsed = WebFetchInput.Create(
        /*lang=json,strict*/ """{"url":"javascript:alert(1)","timeoutSeconds":30}""");
    Assert.False(parsed.IsSuccess);
  }

  [Fact]
  public void MissingUrl_IsRejected()
  {
    Result<WebFetchInput> parsed = WebFetchInput.Create(
        /*lang=json,strict*/ """{"timeoutSeconds":30}""");
    Assert.False(parsed.IsSuccess);
    Assert.Equal("MissingParameter", parsed.Error.Code);
  }

  [Fact]
  public void EmptyUrl_IsRejected()
  {
    Result<WebFetchInput> parsed = WebFetchInput.Create(
        /*lang=json,strict*/ """{"url":"","timeoutSeconds":30}""");
    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidParameterValue", parsed.Error.Code);
  }

  [Fact]
  public void NonStringUrl_IsRejected()
  {
    Result<WebFetchInput> parsed = WebFetchInput.Create(
        /*lang=json,strict*/ """{"url":42,"timeoutSeconds":30}""");
    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidParameterType", parsed.Error.Code);
  }

  [Fact]
  public void UnknownParameter_IsRejected()
  {
    Result<WebFetchInput> parsed = WebFetchInput.Create(
        /*lang=json,strict*/ """{"url":"https://example.com/","timeoutSeconds":30,"format":"markdown"}""");
    Assert.False(parsed.IsSuccess);
    Assert.Equal("UnknownParameter", parsed.Error.Code);
    Assert.Contains("format", parsed.Error.Message, StringComparison.Ordinal);
  }
}
