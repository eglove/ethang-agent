using eThangAgent.CLI;
using Ag = eThangAgent.AgentDomain.Agent;
using eThangAgent.ModelDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.Agent.Application;
using eThangAgent.OpenRouter.ACL;
using eThangAgent.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")
    ?? throw new InvalidOperationException(
        "OPENROUTER_API_KEY environment variable not set. "
        + "Get a key at https://openrouter.ai/keys");

var baseUrlEnv = Environment.GetEnvironmentVariable("OPENROUTER_BASE_URL");
var baseUrl = string.IsNullOrWhiteSpace(baseUrlEnv)
    ? new Uri("https://openrouter.ai")
    : new Uri(baseUrlEnv);

var services = new ServiceCollection()
    .AddSingleton(new OpenRouterConfiguration(apiKey, baseUrl))
    .AddHttpClient<IModelProvider, OpenRouterModelProvider>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(120);
    })
    .Services
    .AddSingleton(_ => ModelConfig.Create("openai/gpt-4o-mini", 1024, 0.7f).Value!)
    .AddSingleton<Conversation>()
    .AddSingleton<IConversationRepository, InMemoryConversationRepository>()
    .AddSingleton<Ag>(sp =>
    {
        var provider = sp.GetRequiredService<IModelProvider>();
        var conversation = sp.GetRequiredService<Conversation>();
        var config = sp.GetRequiredService<ModelConfig>();
        return new Ag(provider, conversation, config);
    })
    .AddSingleton<SendMessageCommandHandler>()
    .BuildServiceProvider();

var handler = services.GetRequiredService<SendMessageCommandHandler>();

Console.WriteLine("eThang Agent - type /exit to quit");
Console.WriteLine();

while (true)
{
    Console.Write("> ");
    var input = Console.ReadLine()?.Trim() ?? string.Empty;
    if (input is "/exit" or "/quit")
        break;
    if (string.IsNullOrWhiteSpace(input))
        continue;

    var result = await handler.Handle(new SendMessageCommand(input));
    Console.WriteLine(result.Match(
        success: response => response,
        failure: error => $"Error [{error.Code}]: {error.Message}"));
    Console.WriteLine();
}
