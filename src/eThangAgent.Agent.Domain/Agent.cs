using eThangAgent.ModelDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ToolDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

public class Agent
{
    private readonly IModelProvider _provider;
    private readonly IToolRegistry _tools;
    private readonly ISystemPromptProvider? _systemPrompt;
    private readonly int _maxToolIterations;

    public Conversation Conversation { get; }
    public ModelConfig Config { get; }

    /// <summary>Identity of this agent. Roots generate one on construction; spawned children carry their persisted id.</summary>
    public AgentId Id { get; }

    /// <summary>Depth in the spawn tree. Root agents are depth 0; children run at parent depth + 1.</summary>
    public int Depth { get; }

    /// <summary>Tool calls executed during the most recent SendMessage; 0 when the turn ended without any.</summary>
    public int LastTurnToolCalls { get; private set; }

    public Agent(IModelProvider provider, Conversation conversation, ModelConfig config,
        IToolRegistry tools, ISystemPromptProvider? systemPrompt = null, int maxToolIterations = 10,
        AgentId? id = null, int depth = 0)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _systemPrompt = systemPrompt;
        Conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
        Config = config ?? throw new ArgumentNullException(nameof(config));
        _maxToolIterations = maxToolIterations;
        Id = id ?? AgentId.NewId();
        Depth = depth;
    }

    public async Task<Result<string>> SendMessage(string text, CancellationToken ct = default)
    {
        LastTurnToolCalls = 0;
        Conversation.AddUserMessage(text);
        for (var i = 0; i < _maxToolIterations; i++)
        {
            var request = new ModelRequest(
                Conversation.Messages, _tools.Definitions, _systemPrompt?.Build());
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
                LastTurnToolCalls++;
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
