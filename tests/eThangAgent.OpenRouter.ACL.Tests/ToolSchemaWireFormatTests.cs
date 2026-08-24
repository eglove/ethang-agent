using System.Net;
using System.Text;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.OpenRouter.ACL.Tests;

public class ToolSchemaWireFormatTests
{
    private static async Task<(string Body, Result<ModelResponse> Result)> CaptureAsync(
        List<ToolDefinition> tools)
    {
        string? body = null;
        var handler = new FakeHttpMessageHandler(async req =>
        {
            body = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}",
                    Encoding.UTF8, "application/json"),
            };
        });
        using var http = new HttpClient(handler);
        var provider = new OpenRouterModelProvider(http,
            new OpenRouterConfiguration("test-key", new Uri("https://openrouter.test")));
        var result = await provider.SendAsync(
            ModelConfig.Create("m", 100, 0.5f).Value!,
            new ModelRequest([new Message(Role.User, "hi", DateTimeOffset.UtcNow)], tools));
        return (body!, result);
    }

    [Fact]
    public async Task StringArrayParameter_IsAdvertisedAsArrayOfStrings()
    {
        var tools = new List<ToolDefinition>
        {
            new("clarify", "desc",
            [
                new ToolParameter("options", ToolParameterType.StringArray, "opts"),
            ]),
        };

        var (body, result) = await CaptureAsync(tools);

        Assert.True(result.IsSuccess);
        Assert.Contains("\"options\":{", body.Replace(" ", ""));
        Assert.Contains("\"type\":\"array\"", body.Replace(" ", ""));
        Assert.Contains("\"items\":{\"type\":\"string\"}", body.Replace(" ", ""));
    }

    [Fact]
    public async Task RequiredList_ComesFromRequiredParameters_NotAllParameters()
    {
        var tools = new List<ToolDefinition>
        {
            new("git_commit", "desc",
            [
                new ToolParameter("style", ToolParameterType.String, "style"),
                new ToolParameter("description", ToolParameterType.String, "subject"),
                new ToolParameter("body", ToolParameterType.String, "body"),
            ], ["style", "description"]),
        };

        var (body, _) = await CaptureAsync(tools);

        Assert.Contains("\"required\":[\"style\",\"description\"]", body.Replace(" ", ""));
        Assert.DoesNotContain("\"required\":[\"style\",\"description\",\"body\"]", body.Replace(" ", ""));
    }
}