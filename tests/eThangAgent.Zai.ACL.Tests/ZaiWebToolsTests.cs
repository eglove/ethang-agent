using System.Net;
using System.Text;
using eThangAgent.ToolDomain;

#pragma warning disable CA2000 // HttpClient owns the handler; tool lifetime bounds it
namespace eThangAgent.Zai.ACL.Tests;

public class ZaiWebToolsTests
{
  private static readonly Uri BaseUrl = new("https://zai.test");
  private static ZaiConfiguration Config => new("test-key", BaseUrl);

  private static RawToolInput Args(string json) => new("test", json);

  private static HttpResponseMessage Json(string body) =>
      new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

  // ---- web_search ----

  [Fact]
  public async Task WebSearch_Requests_SearchPrime_WithQueryAndDefaults()
  {
    string? capturedPath = null;
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      capturedPath = req.RequestUri!.AbsolutePath;
      capturedBody = await req.Content!.ReadAsStringAsync().ConfigureAwait(false);
      return Json(/*lang=json,strict*/ """{"search_result":[{"title":"T1","content":"C1","link":"https://a","media":"wiki","publish_date":"2026-01-01"}]}""");
    });
    ZaiWebSearchTool tool = new(new HttpClient(handler), Config);

    ToolResult result = await tool.ExecuteAsync(Args(
                             /*lang=json,strict*/
                             """{"timeoutSeconds":5,"query":"dotnet"}"""));

    Assert.False(result.IsError);
    Assert.Equal("/paas/v4/web_search", capturedPath);
    Assert.Contains("search-prime", capturedBody, StringComparison.Ordinal);
    Assert.Contains("dotnet", capturedBody, StringComparison.Ordinal);
    Assert.Contains("[web_search 'dotnet': 1 result(s)]", result.Content, StringComparison.Ordinal);
    Assert.Contains("1. T1", result.Content, StringComparison.Ordinal);
    Assert.Contains("   url: https://a", result.Content, StringComparison.Ordinal);
    Assert.Contains("   source: wiki", result.Content, StringComparison.Ordinal);
    Assert.Contains("   published: 2026-01-01", result.Content, StringComparison.Ordinal);
    Assert.Contains("   C1", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task WebSearch_WithApiRootBase_KeepsTheBasePathSegment()
  {
    // Regression (real-API HTTP 404): the default base carries the /api segment; a
    // leading-slash Uri merge would replace the base path and 404 against the real
    // endpoint. Tool endpoints must append to the base path.
    ZaiConfiguration config = new("test-key", new Uri(ZaiConfiguration.DefaultBaseUrl));
    Uri? capturedUrl = null;
    FakeHttpMessageHandler handler = new(req =>
    {
      capturedUrl = req.RequestUri;
      return Task.FromResult(Json(/*lang=json,strict*/ """{"search_result":[]}"""));
    });
    ZaiWebSearchTool tool = new(new HttpClient(handler), config);

    ToolResult result = await tool.ExecuteAsync(Args(
                             /*lang=json,strict*/
                             """{"timeoutSeconds":5,"query":"dotnet"}"""));

    Assert.False(result.IsError);
    Assert.Equal("https://api.z.ai/api/paas/v4/web_search", capturedUrl!.ToString());
  }

  [Fact]
  public async Task WebSearch_SendsRecencyAndCount_WhenProvided()
  {
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      capturedBody = await req.Content!.ReadAsStringAsync().ConfigureAwait(false);
      return Json(/*lang=json,strict*/ """{"search_result":[]}""");
    });
    ZaiWebSearchTool tool = new(new HttpClient(handler), Config);

    ToolResult result = await tool.ExecuteAsync(Args(
                             /*lang=json,strict*/
                             """{"timeoutSeconds":5,"query":"x","count":25,"recency":"oneWeek"}"""));

    Assert.False(result.IsError);
    Assert.Contains("\"count\":25", capturedBody, StringComparison.Ordinal);
    Assert.Contains("oneWeek", capturedBody, StringComparison.Ordinal);
    Assert.Contains("0 result(s)", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task WebSearch_TruncatesLongSnippets_WithVisibleMarker()
  {
    string json = "{\"search_result\":[{\"title\":\"T\",\"content\":\"" + new string('x', 900) +
        "\",\"link\":\"https://a\"}]}";
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Json(json)));
    ZaiWebSearchTool tool = new(new HttpClient(handler), Config);

    ToolResult result = await tool.ExecuteAsync(Args(/*lang=json,strict*/ """{"timeoutSeconds":5,"query":"x"}"""));

    Assert.Contains("[content truncated]", result.Content, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(/*lang=json,strict*/ """{"timeoutSeconds":5}""", "MissingParameter")]
  [InlineData(/*lang=json,strict*/ """{"timeoutSeconds":5,"query":""}""", "InvalidParameterValue")]
  [InlineData(/*lang=json,strict*/ """{"timeoutSeconds":5,"query":"x","count":0}""", "InvalidParameterValue")]
  [InlineData(/*lang=json,strict*/ """{"timeoutSeconds":5,"query":"x","count":51}""", "InvalidParameterValue")]
  [InlineData(/*lang=json,strict*/ """{"timeoutSeconds":5,"query":"x","recency":"yesterday"}""", "InvalidParameterValue")]
  [InlineData(/*lang=json,strict*/ """{"timeoutSeconds":5,"query":"x","bogus":1}""", "UnknownParameter")]
  public async Task WebSearch_RejectsInvalidInput_WithTypedErrors(string json, string code)
  {
    ZaiWebSearchTool tool = new(new HttpClient(new FakeHttpMessageHandler(_ => Task.FromResult(Json("{}")))), Config);

    ToolResult result = await tool.ExecuteAsync(Args(json));

    Assert.True(result.IsError);
    Assert.StartsWith($"Error [{code}]:", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task WebSearch_ProviderError_IsSurfaced()
  {
    FakeHttpMessageHandler handler = new(_ =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
          Content = new StringContent(/*lang=json,strict*/ """{"code":429,"message":"slow down"}""", Encoding.UTF8, "application/json")
        }));
    ZaiWebSearchTool tool = new(new HttpClient(handler), Config);

    ToolResult result = await tool.ExecuteAsync(Args(/*lang=json,strict*/ """{"timeoutSeconds":5,"query":"x"}"""));

    Assert.True(result.IsError);
    Assert.Contains("HTTP 429", result.Content, StringComparison.Ordinal);
    Assert.Contains("slow down", result.Content, StringComparison.Ordinal);
  }

  // ---- web_read ----

  [Fact]
  public async Task WebRead_ReturnsTitleAndContent()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Json(
                             /*lang=json,strict*/
                             """{"reader_result":{"title":"Example","url":"https://a","content":"page body"}}""")));
    ZaiWebReaderTool tool = new(new HttpClient(handler), Config);

    ToolResult result = await tool.ExecuteAsync(Args(
                             /*lang=json,strict*/
                             """{"timeoutSeconds":5,"url":"https://a.example.com/page"}"""));

    Assert.False(result.IsError);
    Assert.Contains("[web_read 'Example' from https://a.example.com/page]", result.Content, StringComparison.Ordinal);
    Assert.Contains("page body", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task WebRead_Truncates_WithCountLine()
  {
    string json = "{\"reader_result\":{\"title\":\"T\",\"content\":\"" + new string('y', 30_001) + "\"}}";
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Json(json)));
    ZaiWebReaderTool tool = new(new HttpClient(handler), Config);

    ToolResult result = await tool.ExecuteAsync(Args(/*lang=json,strict*/ """{"timeoutSeconds":5,"url":"https://a.example.com"}"""));

    Assert.Contains("[truncated at 30000 of 30001 characters]", result.Content, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(/*lang=json,strict*/ """{"timeoutSeconds":5}""", "MissingParameter")]
  [InlineData(/*lang=json,strict*/ """{"timeoutSeconds":5,"url":"not-a-url"}""", "InvalidParameterValue")]
  [InlineData(/*lang=json,strict*/ """{"timeoutSeconds":5,"url":"ftp://a.example.com"}""", "InvalidParameterValue")]
  public async Task WebRead_RejectsInvalidInput(string json, string code)
  {
    ZaiWebReaderTool tool = new(new HttpClient(new FakeHttpMessageHandler(_ => Task.FromResult(Json("{}")))), Config);

    ToolResult result = await tool.ExecuteAsync(Args(json));

    Assert.True(result.IsError);
    Assert.StartsWith($"Error [{code}]:", result.Content, StringComparison.Ordinal);
  }

  // ---- count_tokens ----

  [Fact]
  public async Task CountTokens_ReportsTotals()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Json(
                             /*lang=json,strict*/
                             """{"id":"t","usage":{"prompt_tokens":7,"total_tokens":7}}""")));
    ZaiTokenizerTool tool = new(new HttpClient(handler), Config);

    ToolResult result = await tool.ExecuteAsync(Args(
                             /*lang=json,strict*/
                             """{"timeoutSeconds":5,"text":"hello world"}"""));

    Assert.False(result.IsError);
    Assert.Equal("[count_tokens glm-4.6: 7 token(s) total, 7 prompt token(s)]", result.Content);
  }

  [Theory]
  [InlineData(/*lang=json,strict*/ """{"timeoutSeconds":5}""", "MissingParameter")]
  [InlineData(/*lang=json,strict*/ """{"timeoutSeconds":5,"text":""}""", "InvalidParameterValue")]
  [InlineData(/*lang=json,strict*/ """{"timeoutSeconds":5,"text":"x","model":"glm-9"}""", "InvalidParameterValue")]
  public async Task CountTokens_RejectsInvalidInput(string json, string code)
  {
    ZaiTokenizerTool tool = new(new HttpClient(new FakeHttpMessageHandler(_ => Task.FromResult(Json("{}")))), Config);

    ToolResult result = await tool.ExecuteAsync(Args(json));

    Assert.True(result.IsError);
    Assert.StartsWith($"Error [{code}]:", result.Content, StringComparison.Ordinal);
  }
}
