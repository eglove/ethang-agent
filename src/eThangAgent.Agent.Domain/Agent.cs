using eThangAgent.Model.Domain;
using eThangAgent.Conversation.Domain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Domain;

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
        var result = await _provider.SendAsync(Config, text, ct);
        if (result.IsSuccess)
            Conversation.AddAssistantMessage(result.Value!);
        return result;
    }
}
