using System.Net;
using System.Text;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

public class ToolSchemaWireFormatTests
{
  private static async Task<(string Body, Result<ModelResponse> Result)> CaptureAsync(
      List<ToolDefinition> tools)
  {
    string? body = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      body = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent(
                                       /*lang=json,strict*/
                                       "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}",
                  Encoding.UTF8, "application/json"),
      };
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http,
        new OpenRouterConfiguration("test-key", new Uri("https://openrouter.test")));
    Result<ModelResponse> result = await provider.SendAsync(
        ModelConfig.Create("m", null, 100, 0.5f, 4096).Value!,
        new ModelRequest([new Message(Role.User, "hi", DateTimeOffset.UtcNow)], tools)).ConfigureAwait(true);
    return (body!, result);
  }

  [Fact]
  public async Task StringArrayParameter_IsAdvertisedAsArrayOfStrings()
  {
    List<ToolDefinition> tools =
        [
            new("clarify", "desc",
            [
                new ToolParameter("options", ToolParameterType.TextArray, "opts"),
            ]),
        ];

    (string? body, Result<ModelResponse>? result) = await CaptureAsync(tools);

    Assert.True(result.IsSuccess);
    Assert.Contains("\"options\":{", body.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
    Assert.Contains("\"type\":\"array\"", body.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
    Assert.Contains("\"items\":{\"type\":\"string\"}", body.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
  }

  [Fact]
  public async Task RequiredList_ComesFromRequiredParameters_NotAllParameters()
  {
    List<ToolDefinition> tools =
        [
            new("git_commit", "desc",
            [
                new ToolParameter("style", ToolParameterType.Text, "style"),
                new ToolParameter("description", ToolParameterType.Text, "subject"),
                new ToolParameter("body", ToolParameterType.Text, "body"),
            ], ["style", "description"]),
        ];

    (string? body, Result<ModelResponse> _) = await CaptureAsync(tools);

    Assert.Contains("\"required\":[\"style\",\"description\"]", body.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
    Assert.DoesNotContain("\"required\":[\"style\",\"description\",\"body\"]", body.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
  }
}
