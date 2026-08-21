using eThangAgent.ModelDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ToolDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

public class Agent
{
    private readonly IModelProvider _provider;
    private readonly IToolRegistry _tools;
    private readonly int _maxToolIterations;

    public Conversation Conversation { get; }
    public ModelConfig Config { get; }

    public Agent(IModelProvider provider, Conversation conversation, ModelConfig config,
        IToolRegistry tools, int maxToolIterations = 10)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        Conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
        Config = config ?? throw new ArgumentNullException(nameof(config));
        _maxToolIterations = maxToolIterations;
    }

    public async Task<Result<string>> SendMessage(string text, CancellationToken ct = default)
    {
        Conversation.AddUserMessage(text);
        for (var i = 0; i < _maxToolIterations; i++)
        {
            var request = new ModelRequest(Conversation.Messages, _tools.Definitions);
            var result = await _provider.SendAsync(Config, request, ct);
            if (!result.IsSuccess)
                return Result<string>.Failure(result.Error!);

            var response = result.Value!;
            if (response.ToolCalls.Count == 0)
            {
                var content = response.Content ?? "";
                Conversation.AddAssistantMessage(content);
                return Result<string>.Success(content);
            }

            Conversation.AddAssistantMessage(response.Content ?? "",
                response.ToolCalls
                    .Select(tc => new ToolCall(tc.Id, tc.Name, tc.Arguments))
                    .ToList());

            foreach (var call in response.ToolCalls)
            {
                var tool = _tools.Find(call.Name);
                var toolResult = tool is null
                    ? new ToolResult($"Error [UnknownTool]: Unknown tool: {call.Name}.", true)
                    : await tool.ExecuteAsync(new RawToolInput(call.Name, call.Arguments), ct);
                Conversation.AddToolResult(call.Id, toolResult.Content);
            }
        }

        return Result<string>.Failure(new Error("MaxToolIterations",
            $"Tool loop did not converge after {_maxToolIterations} iterations."));
    }
}
