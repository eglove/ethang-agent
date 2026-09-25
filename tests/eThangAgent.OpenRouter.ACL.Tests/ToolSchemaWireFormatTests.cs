using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

// The Responses API's flat function-tool shape: name/description/parameters sit at
// the entry's top level (no nested "function" object), and the "items" schema key
// appears only for array parameters — a null items key was tolerated on
// chat-completions but this surface validates schemas strictly.
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
      return Wire.Ok();
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http,
        new OpenRouterConfiguration("test-key", new Uri("https://openrouter.test")));
    Result<ModelResponse> result = await provider.SendAsync(
        ModelConfig.Create("m", null, 100, 0.5f, 4096).Value!,
        new ModelRequest([new Message(Role.User, "hi", DateTimeOffset.UtcNow)], tools)).ConfigureAwait(true);
    return (body!, result);
  }

  private static async Task<JsonElement> SingleToolAsync(List<ToolDefinition> tools)
  {
    (string body, Result<ModelResponse> result) = await CaptureAsync(tools).ConfigureAwait(true);
    Assert.True(result.IsSuccess);
    using JsonDocument doc = JsonDocument.Parse(body);
    JsonElement toolsElement = doc.RootElement.GetProperty("tools");
    _ = Assert.Single(toolsElement.EnumerateArray());
    JsonElement tool = toolsElement[0];
    // Clone: the doc is disposed when this method returns.
    return tool.Clone();
  }

  [Fact]
  public async Task FunctionTool_IsFlat_NoNestedFunctionObject()
  {
    JsonElement tool = await SingleToolAsync(
        [
            new("demo_tool", "desc",
            [
                new ToolParameter("options", ToolParameterType.Text, "opts"),
            ]),
        ]).ConfigureAwait(true);

    Assert.Equal("function", tool.GetProperty("type").GetString());
    Assert.Equal("demo_tool", tool.GetProperty("name").GetString());
    Assert.Equal("desc", tool.GetProperty("description").GetString());
    Assert.False(tool.TryGetProperty("function", out _));
  }

  [Fact]
  public async Task StringArrayParameter_IsAdvertisedAsArrayOfStrings()
  {
    List<ToolDefinition> tools =
        [
            new("demo_tool", "desc",
            [
                new ToolParameter("options", ToolParameterType.TextArray, "opts"),
            ]),
        ];

    (string? body, Result<ModelResponse>? result) = await CaptureAsync(tools).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    Assert.Contains("\"options\":{", body.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
    Assert.Contains("\"type\":\"array\"", body.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
    Assert.Contains("\"items\":{\"type\":\"string\"}", body.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
  }

  [Fact]
  public async Task ScalarParameter_OmitsTheItemsKey()
  {
    JsonElement tool = await SingleToolAsync(
        [
            new("demo_tool", "desc",
            [
                new ToolParameter("path", ToolParameterType.Text, "file path"),
            ]),
        ]).ConfigureAwait(true);

    JsonElement path = tool.GetProperty("parameters").GetProperty("properties").GetProperty("path");
    Assert.Equal("string", path.GetProperty("type").GetString());
    Assert.False(path.TryGetProperty("items", out _));
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

    (string? body, Result<ModelResponse> _) = await CaptureAsync(tools).ConfigureAwait(true);

    Assert.Contains("\"required\":[\"style\",\"description\"]", body.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
    Assert.DoesNotContain("\"required\":[\"style\",\"description\",\"body\"]", body.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
  }
}
