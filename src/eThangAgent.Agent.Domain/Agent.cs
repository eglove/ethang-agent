using eThangAgent.ModelDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

public class Agent
{
    private readonly IModelProvider _provider;

    public Conversation Conversation { get; }
    public ModelConfig Config { get; }

    public Agent(IModelProvider provider, Conversation conversation, ModelConfig config)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        Conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
        Config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public async Task<Result<string>> SendMessage(string text, CancellationToken ct = default)
    {
        Conversation.AddUserMessage(text);
        var request = new ModelRequest(Conversation.Messages);
        var result = await _provider.SendAsync(Config, request, ct);
        if (!result.IsSuccess)
            return Result<string>.Failure(result.Error!);
        Conversation.AddAssistantMessage(result.Value!.Content ?? "");
        return Result<string>.Success(result.Value.Content ?? "");
    }
}
